# yt-dlp PyInstaller runtime hook
#
# Activated only when the environment variable YTDLP_RELAX_SECLEVEL=1 is set.
# Two effects, both applied right before every TLS handshake via wrap_socket
# and wrap_bio:
#
#   1. Lowers OpenSSL's per-context security level to 0 via set_ciphers
#      ('DEFAULT@SECLEVEL=0'). Allows TLS handshakes to complete with leaf
#      certificates whose key sizes the default SECLEVEL=1 would reject.
#
#   2. Disables peer-certificate verification on the context
#      (verify_mode=CERT_NONE, check_hostname=False). v1.3.0-alpha5 added
#      this because the host application's --no-check-certificates CLI
#      flag passed to yt-dlp does NOT cover every code path inside yt-dlp:
#      its InnerTube API client and certain extractor-internal HTTP calls
#      create their own SSLContext that doesn't inherit the global
#      nocheckcertificate setting. Patching at wrap_socket/wrap_bio is
#      the universal choke point — every TLS connection in Python ssl
#      goes through one of those, so doing it here covers the cases
#      --no-check-certificates misses.
#
# When the env var is unset, the hook is a no-op and yt-dlp behaves exactly
# like upstream's prebuilt binary.
#
# Order matters: set check_hostname=False BEFORE verify_mode=CERT_NONE,
# because if check_hostname is True you cannot lower verify_mode to
# CERT_NONE (Python ssl raises ValueError).
#
# Source location: build/yt-dlp-patch/ytdlp_seclevel_hook.py
# Repo:            https://github.com/AwesomeBigBoy/YTDownloadTool

import os

if os.environ.get('YTDLP_RELAX_SECLEVEL') == '1':
    try:
        import ssl

        _original_wrap_socket = ssl.SSLContext.wrap_socket
        _original_wrap_bio    = getattr(ssl.SSLContext, 'wrap_bio', None)
        _original_set_ciphers = ssl.SSLContext.set_ciphers

        def _relax_context(ctx):
            try:
                _original_set_ciphers(ctx, 'DEFAULT@SECLEVEL=0')
            except ssl.SSLError:
                pass
            try:
                ctx.check_hostname = False
                ctx.verify_mode = ssl.CERT_NONE
            except (AttributeError, ValueError):
                pass

        def _patched_wrap_socket(self, *args, **kwargs):
            _relax_context(self)
            return _original_wrap_socket(self, *args, **kwargs)

        def _patched_wrap_bio(self, *args, **kwargs):
            _relax_context(self)
            return _original_wrap_bio(self, *args, **kwargs)

        ssl.SSLContext.wrap_socket = _patched_wrap_socket
        if _original_wrap_bio is not None:
            ssl.SSLContext.wrap_bio = _patched_wrap_bio
    except ImportError:
        pass
