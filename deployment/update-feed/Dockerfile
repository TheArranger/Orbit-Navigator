FROM nginx@sha256:5616878291a2eed594aee8db4dade5878cf7edcb475e59193904b198d9b830de

COPY --chown=101:101 nginx.conf /etc/nginx/nginx.conf

USER 101:101
ENTRYPOINT ["nginx"]
CMD ["-c", "/etc/nginx/nginx.conf", "-g", "daemon off;"]
