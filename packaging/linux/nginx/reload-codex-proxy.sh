#!/bin/sh
# Certbot deploy hook: retain the running configuration if validation fails.
set -eu
/usr/sbin/nginx -t
/usr/bin/systemctl reload nginx
