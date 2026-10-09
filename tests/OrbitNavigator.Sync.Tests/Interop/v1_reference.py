"""Independent v1 wire/crypto reference and disposable, loopback-only provider E2E.

This is test code, never an authentication provider or production client. It uses
the real My Orbit OAuth/sync routers with a fixture-only browser identity; it does
not claim to sign a real user in or exercise production login/MFA or native UI.
No My Orbit app/config/.env is imported. Existing DBs/profiles are never opened.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import secrets
import socket
import sqlite3
import struct
import sys
import tempfile
import threading
import time
import uuid
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace
from urllib.parse import parse_qs, urlsplit

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

SCOPES = "orbit.navigator.link orbit.sync.history orbit.sync.open_tabs orbit.sync.devices"
PROFILE = "22222222222222222222222222222222"
KEYSET = "33333333333333333333333333333333"
ENTITY = "55555555555555555555555555555555"
ROOT = bytes(range(32))  # Public deterministic test key, never a deployment key.
CODE = "ABCD-EFGH-JKLM-NPQR-STUV"  # Public deterministic test code.
TICKS = 639269712000000000  # 2026-10-07T12:00:00Z, .NET UTC ticks.


class ProtocolStatusFailure(AssertionError):
    def __init__(self, operation: str, status: int) -> None:
        # operation is a fixed source-code label, never a URI or provider text.
        self.operation = operation
        self.status = status
        super().__init__(f"{operation}: unexpected HTTP status {status}")


def require_status(response, expected: int, operation: str) -> None:
    if response.status_code != expected:
        raise ProtocolStatusFailure(operation, response.status_code)


def b64(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")


def unb64(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def text(value: str) -> bytes:
    encoded = value.encode("utf-8", errors="strict")
    return struct.pack("<i", len(encoded)) + encoded


def aad(device: str, category: str, sequence: int, kind: str = "upsert") -> dict:
    return {
        "protocol_version": 1, "schema_version": 1, "profile_id": PROFILE,
        "device_id": device, "keyset_id": KEYSET, "key_epoch": 0,
        "record_kind": kind, "envelope_id": uuid.uuid4().hex, "category": category,
        "entity_id": ENTITY, "operation_id": None, "client_generation": 0,
        "client_sequence": sequence,
    }


def canonical(metadata: dict) -> bytes:
    # Canonical crypto names deliberately differ from JSON wire enum names.
    category = {"history": "history", "open_tabs": "opentabs"}[metadata["category"]]
    return ("orbit-navigator|sync-aad|protocol=1|schema=1"
            f"|profile={metadata['profile_id']}|device={metadata['device_id']}"
            f"|keyset={metadata['keyset_id']}|keyepoch={metadata['key_epoch']}"
            f"|kind={metadata['record_kind']}|envelope={metadata['envelope_id']}"
            f"|category={category}|entity={metadata['entity_id']}|operation=-"
            f"|clientgeneration={metadata['client_generation']}"
            f"|clientsequence={metadata['client_sequence']}").encode("utf-8")


def category_key(root: bytes, metadata: dict) -> bytes:
    salt = (b"orbit-navigator|sync-key-derivation|v1" + bytes.fromhex(metadata["keyset_id"])
            + struct.pack(">q", metadata["key_epoch"]))
    category = {"history": "history", "open_tabs": "open-tabs"}[metadata["category"]]
    info = f"orbit-navigator|sync-category|{category}|v1".encode("ascii")
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=salt, info=info).derive(root)


def payload(category: str, kind: str = "upsert") -> bytes:
    number = {"history": 0, "open_tabs": 2}[category]
    if kind == "tombstone":
        return struct.pack("<IBB", 0x4F4E5354, 1, number) + bytes.fromhex(ENTITY) + struct.pack("<q", 0)
    common = (struct.pack("<IBB", 0x4F4E5352, 1, number) + bytes.fromhex(ENTITY)
              + struct.pack("<qq", 7, TICKS) + text("https://fixture.invalid/never-upload-plaintext")
              + text("Fixture title \u03a9 \U0001f680"))
    return (common + struct.pack("<qi", TICKS, 4) if category == "history"
            else common + struct.pack("<iB", 3, 1) + text("Fixture group"))


def encrypt(root: bytes, metadata: dict, plaintext: bytes, nonce: bytes | None = None) -> dict:
    nonce = nonce or secrets.token_bytes(12)
    encrypted = AESGCM(category_key(root, metadata)).encrypt(nonce, plaintext, canonical(metadata))
    return {"aad": metadata, "nonce": b64(nonce), "ciphertext": b64(encrypted[:-16]),
            "authentication_tag": b64(encrypted[-16:])}


def decrypt(root: bytes, record: dict) -> bytes:
    return AESGCM(category_key(root, record["aad"])).decrypt(
        unb64(record["nonce"]), unb64(record["ciphertext"]) + unb64(record["authentication_tag"]),
        canonical(record["aad"]))


def recovery_aad(profile: str, wrapped: dict) -> bytes:
    kdf = wrapped["kdf"]
    return (f"orbit-navigator|recovery-key-wrap|version=1|profile={profile}"
            f"|keyset={wrapped['keyset_id']}|generation={wrapped['generation']}"
            f"|wrap=recoverycode|kdf=pbkdf2sha256|iterations={kdf['iterations']}"
            f"|memorykib=0|parallelism=1|derivedbytes=32|salt={unb64(kdf['salt']).hex().upper()}").encode("ascii")


def wrap(root: bytes, code: str, deterministic: bool = False) -> dict:
    salt = bytes(range(32)) if deterministic else secrets.token_bytes(32)
    nonce = bytes(range(12)) if deterministic else secrets.token_bytes(12)
    wrapped = {"keyset_id": KEYSET, "generation": 0, "wrap_method": "recovery_code",
               "kdf": {"algorithm": "pbkdf2_sha256", "salt": b64(salt), "iterations": 600000,
                       "memory_kib": 0, "parallelism": 1, "derived_key_size_bytes": 32}, "nonce": b64(nonce)}
    key = hashlib.pbkdf2_hmac("sha256", code.encode("ascii"), salt, 600000, 32)
    encrypted = AESGCM(key).encrypt(nonce, root, recovery_aad(PROFILE, wrapped))
    return wrapped | {"wrapped_key_ciphertext": b64(encrypted[:-16]), "authentication_tag": b64(encrypted[-16:])}


def unwrap(profile: str, wrapped: dict, code: str) -> bytes:
    key = hashlib.pbkdf2_hmac("sha256", code.encode("ascii"), unb64(wrapped["kdf"]["salt"]), 600000, 32)
    return AESGCM(key).decrypt(unb64(wrapped["nonce"]), unb64(wrapped["wrapped_key_ciphertext"])
                              + unb64(wrapped["authentication_tag"]), recovery_aad(profile, wrapped))


def vectors() -> dict:
    records = []
    for index, (category, kind) in enumerate((("history", "upsert"), ("open_tabs", "upsert"), ("history", "tombstone"))):
        metadata = aad("1" * 32, category, index, kind)
        metadata["envelope_id"] = str(index + 6) * 32
        plaintext = payload(category, kind)
        record = encrypt(ROOT, metadata, plaintext, bytes([index + 1]) * 12)
        records.append({"record": record, "canonical_aad": canonical(metadata).decode("ascii"),
                        "plaintext_hex": plaintext.hex(), "category_key_hex": category_key(ROOT, metadata).hex()})
    return {"fixture_only": True, "profile_id": PROFILE, "root_key_hex": ROOT.hex(),
            "recovery_code": CODE, "wrapped_keyset": wrap(ROOT, CODE, True), "records": records}


def run_e2e(provider_root: Path) -> dict:
    import httpx
    import uvicorn
    from fastapi import FastAPI, Form, Request
    from fastapi.responses import HTMLResponse, Response

    sys.path.insert(0, str(provider_root.resolve(strict=True)))
    from myorbit.navigator_authorization import NavigatorAuthorizationService, create_navigator_authorization_router
    from myorbit.navigator_sync import NavigatorOpaqueSyncService, create_navigator_sync_router

    with tempfile.TemporaryDirectory(prefix="orbit-sync-e2e-") as directory:
        database = Path(directory) / "fixture.sqlite3"

        @contextmanager
        def sessions():
            connection = sqlite3.connect(database)
            connection.row_factory = sqlite3.Row
            connection.execute("PRAGMA foreign_keys=ON")
            try:
                yield connection
                connection.commit()
            except Exception:
                connection.rollback()
                raise
            finally:
                connection.close()

        with sessions() as connection:
            connection.execute("CREATE TABLE admins(id INTEGER PRIMARY KEY, username TEXT NOT NULL, active INTEGER NOT NULL)")
            connection.execute("INSERT INTO admins VALUES(1, 'disposable-e2e-fixture', 1)")
        authorization = NavigatorAuthorizationService(sessions)
        authorization.ensure_schema()
        relay = NavigatorOpaqueSyncService(sessions)
        relay.ensure_schema()
        app = FastAPI()
        browser_secret = secrets.token_urlsafe(32)
        fixture_password = secrets.token_urlsafe(32)

        def current_actor(request):
            cookie = request.cookies.get("fixture_browser_session", "")
            return SimpleNamespace(id=1) if secrets.compare_digest(cookie, browser_secret) else None

        # Explicit test-only authentication boundary, not a replacement for My Orbit login/MFA.
        @app.post("/fixture/login")
        def fixture_login(password: str = Form("")):
            if not secrets.compare_digest(password, fixture_password):
                return Response(status_code=401)
            response = Response(status_code=204)
            response.set_cookie("fixture_browser_session", browser_secret, httponly=True, samesite="strict")
            return response

        app.include_router(create_navigator_authorization_router(
            authorization, current_actor=current_actor,
            render=lambda request, template, context: HTMLResponse("fixture consent"),
            request_is_https=lambda request: False))
        app.include_router(create_navigator_sync_router(relay, authorization))
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(128)
        origin = f"http://127.0.0.1:{listener.getsockname()[1]}"
        server = uvicorn.Server(uvicorn.Config(app, log_level="critical", access_log=False))
        thread = threading.Thread(target=lambda: server.run(sockets=[listener]), daemon=True)
        thread.start()
        deadline = time.monotonic() + 15
        while not server.started:
            if time.monotonic() > deadline:
                raise RuntimeError("Disposable provider failed to start")
            time.sleep(0.02)

        try:
            with httpx.Client(base_url=origin, trust_env=False, follow_redirects=False) as protocol:
                def authorize(scope: str, name: str) -> dict:
                    verifier = secrets.token_urlsafe(48)
                    state = secrets.token_urlsafe(32)
                    redirect = "http://127.0.0.1:49152/my-orbit/callback"
                    pushed = protocol.post("/oauth2/par", data={
                        "client_id": "orbit-navigator", "redirect_uri": redirect, "scope": scope,
                        "state": state, "code_challenge": b64(hashlib.sha256(verifier.encode()).digest()),
                        "code_challenge_method": "S256", "response_type": "code", "device_name": name})
                    assert pushed.status_code == 201
                    with httpx.Client(base_url=origin, trust_env=False, follow_redirects=False) as browser:
                        start = browser.get("/oauth2/authorize", params={"client_id": "orbit-navigator",
                                                                                 "request_uri": pushed.json()["request_uri"]})
                        assert start.status_code == 303 and start.headers["location"].startswith("/login")
                        assert browser.post("/fixture/login", data={"password": "wrong"}).status_code == 401
                        assert browser.post("/fixture/login", data={"password": fixture_password}).status_code == 204
                        approved = browser.post("/oauth2/authorize/decision", data={"decision": "approve"})
                        assert approved.status_code == 303
                    query = parse_qs(urlsplit(approved.headers["location"]).query)
                    assert query["state"] == [state]
                    form = {"grant_type": "authorization_code", "client_id": "orbit-navigator",
                            "code": query["code"][0], "redirect_uri": redirect, "code_verifier": verifier}
                    assert protocol.post("/oauth2/token", data=form | {"code_verifier": "wrong" * 16}).status_code == 400
                    issued = protocol.post("/oauth2/token", data=form)
                    require_status(issued, 200, "authorization-code-exchange")
                    assert protocol.post("/oauth2/token", data=form).status_code == 400
                    assert issued.json()["scope"] == scope
                    return issued.json()

                def headers(tokens):
                    return {"Authorization": "Bearer " + tokens["access_token"]}

                def register(tokens, name):
                    response = protocol.post("/navigator-sync/v1/devices/register", headers=headers(tokens),
                                             json={"display_name": name, "public_identity_key": b64(secrets.token_bytes(32))})
                    require_status(response, 200, "device-registration")
                    assert response.headers["cache-control"] == "no-store"
                    assert "set-cookie" not in response.headers
                    result = response.json()
                    assert result["device_id"] != tokens["connection_id"]
                    return result

                link = authorize("orbit.navigator.link", "Fixture link only")
                assert protocol.post("/navigator-sync/v1/devices/register", headers=headers(link), json={}).status_code == 401
                first = authorize(SCOPES, "Fixture desktop")
                second = authorize(SCOPES, "Fixture joining device")
                device_a, device_b = register(first, "Desktop"), register(second, "Joining")
                root = secrets.token_bytes(32)
                code = CODE  # Public fixture code; root and all credentials are disposable.
                wrapped = wrap(root, code)
                uploaded = protocol.put("/navigator-sync/v1/keysets/recovery-envelope", headers=headers(first), json={
                    "device_id": device_a["device_id"], "fence": device_a["fence"], "profile_id": PROFILE,
                    "envelope_revision": 1, "replaces_revision": 0, "wrapped_keyset": wrapped})
                require_status(uploaded, 200, "recovery-envelope-upload")

                def query(device):
                    return {"device_id": device["device_id"], "client_generation": 0, "minimum_accepted_generation": 0}

                downloaded = protocol.get("/navigator-sync/v1/keysets/recovery-envelope", headers=headers(second), params=query(device_b))
                require_status(downloaded, 200, "recovery-envelope-download")
                recovered = unwrap(downloaded.json()["profile_id"], downloaded.json()["wrapped_keyset"], code)
                assert recovered == root
                batch = [encrypt(root, aad(device_a["device_id"], category, index), payload(category))
                         for index, category in enumerate(("history", "open_tabs"))]
                push_payload = {"device_id": device_a["device_id"], "fence": device_a["fence"], "records": batch}
                pushed = protocol.post("/navigator-sync/v1/records/push", headers=headers(first), json=push_payload)
                require_status(pushed, 200, "encrypted-record-push")
                assert protocol.post("/navigator-sync/v1/records/push", headers=headers(first), json=push_payload).status_code == 200
                pulled = protocol.get("/navigator-sync/v1/records/pull", headers=headers(second), params=query(device_b) | {"maximum_items": 100})
                require_status(pulled, 200, "encrypted-record-pull")
                received = pulled.json()["records"]
                assert len(received) == 2
                for item in received:
                    assert decrypt(recovered, item) == payload(item["aad"]["category"])
                tombstone = encrypt(recovered, aad(device_b["device_id"], "history", 0, "tombstone"), payload("history", "tombstone"))
                assert protocol.post("/navigator-sync/v1/records/push", headers=headers(second), json={
                    "device_id": device_b["device_id"], "fence": device_b["fence"], "records": [tombstone]}).status_code == 200
                back = protocol.get("/navigator-sync/v1/records/pull", headers=headers(first), params=query(device_a) | {"maximum_items": 100})
                assert any(item["aad"]["record_kind"] == "tombstone" and decrypt(root, item) == payload("history", "tombstone")
                           for item in back.json()["records"])
                stale = push_payload | {"fence": device_a["fence"] | {"client_generation": 1}}
                assert protocol.post("/navigator-sync/v1/records/push", headers=headers(first), json=stale).status_code >= 400
                assert protocol.post("/navigator-sync/v1/records/push", headers=headers(first) | {"Cookie": "forbidden=1"}, json=push_payload).status_code == 401

                # Production OAuth refresh rotation/reuse revokes the entire second connection.
                refresh_form = {"client_id": "orbit-navigator", "grant_type": "refresh_token", "refresh_token": second["refresh_token"]}
                rotated = protocol.post("/oauth2/token", data=refresh_form)
                assert rotated.status_code == 200
                reused = protocol.post("/oauth2/token", data=refresh_form)
                assert reused.status_code == 400 and reused.json()["error"] == "invalid_grant"
                assert protocol.get("/navigator-sync/v1/records/pull", headers=headers(rotated.json()), params=query(device_b)).status_code == 401
                assert protocol.post("/oauth2/revoke", data={"client_id": "orbit-navigator", "token": first["refresh_token"]}).status_code == 200
                assert protocol.get("/navigator-sync/v1/records/pull", headers=headers(first), params=query(device_a)).status_code == 401
                assert not protocol.cookies

            with sessions() as connection:
                stored = "\n".join(connection.iterdump()).encode("utf-8")
            for forbidden in (b"fixture.invalid", b"Fixture title", CODE.encode(), root, first["access_token"].encode(),
                              first["refresh_token"].encode(), second["access_token"].encode()):
                assert forbidden not in stored
            return {"passed": True, "provider": "real local OAuth and opaque relay routers over loopback HTTP",
                    "identity": "disposable fixture actor; not production login/MFA or real-user proof",
                    "clients": 2, "checks": ["link-only cannot sync", "PAR/PKCE/state/code-replay",
                        "recovery unwrap", "history/open-tabs encryption", "bidirectional tombstone",
                        "idempotent push", "stale fence", "cookie rejection", "refresh reuse revocation",
                        "explicit revocation", "provider DB plaintext/credential exclusion"],
                    "temporary_data": "removed on exit", "native_app_composition": "not exercised"}
        finally:
            server.should_exit = True
            thread.join(timeout=10)
            listener.close()
            if thread.is_alive():
                raise RuntimeError("Disposable provider did not stop")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--provider-root", type=Path)
    parser.add_argument("--vectors", action="store_true")
    args = parser.parse_args()
    if args.vectors:
        print(json.dumps(vectors(), indent=2, ensure_ascii=True))
    elif args.provider_root:
        try:
            print(json.dumps(run_e2e(args.provider_root), indent=2))
        except ProtocolStatusFailure as failure:
            print(json.dumps({"passed": False, "operation": failure.operation, "status": failure.status}))
            sys.exit(1)
        except Exception as failure:
            # HTTP/JSON/provider exception strings can include response content or
            # URLs with authorization handles. Never print them or a traceback.
            print(json.dumps({"passed": False, "failure_type": type(failure).__name__}))
            sys.exit(1)
    else:
        parser.error("select --vectors or --provider-root")
