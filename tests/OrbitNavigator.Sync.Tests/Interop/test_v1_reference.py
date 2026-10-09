import unittest

from v1_reference import ProtocolStatusFailure, require_status


class ResponseWithSensitiveBody:
    status_code = 503

    @property
    def text(self):
        raise AssertionError("Sensitive provider body must never be read for diagnostic output")


class ProtocolDiagnosticTests(unittest.TestCase):
    def test_failed_status_contains_only_fixed_operation_and_status(self):
        with self.assertRaises(ProtocolStatusFailure) as raised:
            require_status(ResponseWithSensitiveBody(), 200, "authorization-code-exchange")
        self.assertEqual("authorization-code-exchange: unexpected HTTP status 503", str(raised.exception))
        self.assertEqual("authorization-code-exchange", raised.exception.operation)
        self.assertEqual(503, raised.exception.status)

    def test_success_does_not_inspect_provider_body(self):
        require_status(ResponseWithSensitiveBody(), 503, "authorization-code-exchange")


if __name__ == "__main__":
    unittest.main()
