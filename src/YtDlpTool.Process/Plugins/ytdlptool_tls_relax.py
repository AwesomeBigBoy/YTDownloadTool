# YtDlpTool TLS relaxation plugin for yt-dlp
#
# Loaded by the OFFICIAL, unmodified yt-dlp build through yt-dlp's plugin
# system (--plugin-dirs). YtDlpTool writes this file to disk at startup and
# passes --plugin-dirs only when the user has turned on
# 「允許不受信任憑證」 (AllowUntrustedCertificates). Even then it does nothing
# unless the environment variable YTDLP_RELAX_SECLEVEL=1 is also set, so a
# copy of this file found by accident is inert.
#
# Why a plugin and not a patched yt-dlp.exe (v1.5.0):
#   Up to v1.4.0 this code was a PyInstaller runtime hook baked into our own
#   build of yt-dlp.exe. That build has a hash nobody else in the world has,
#   no signature and no reputation, and endpoint security treats exactly that
#   profile as suspicious. An endpoint-protection agent's behaviour monitoring
#   held every launch for ~27 s and then refused it with "Access denied" — the
#   official build, started the same way on the same machine, ran in under a
#   second. Shipping upstream's binary byte-for-byte and carrying our one
#   customisation as plain Python keeps the reputation of the official build.
#
# What it does when active, right before every TLS handshake (wrap_socket and
# wrap_bio are the choke points every Python ssl connection passes through):
#   1. set_ciphers('DEFAULT@SECLEVEL=0') — yt-dlp's make_ssl_context pins
#      '@SECLEVEL=2', which rejects HTTPS-inspection proxies whose generated
#      leaf certificates use short, legacy-key-size RSA keys.
#   2. check_hostname=False, verify_mode=CERT_NONE — --no-check-certificates
#      does not reach every SSLContext yt-dlp creates internally.
# check_hostname must be cleared before verify_mode can be lowered, otherwise
# Python's ssl raises ValueError.
#
# The module defines no extractor or postprocessor classes; importing it is
# the whole effect. Source: src/YtDlpTool.Process/Plugins/ytdlptool_tls_relax.py

import os

if os.environ.get('YTDLP_RELAX_SECLEVEL') == '1':
    try:
        import ssl

        _original_wrap_socket = ssl.SSLContext.wrap_socket
        _original_wrap_bio = getattr(ssl.SSLContext, 'wrap_bio', None)
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

        if not getattr(ssl.SSLContext, '_ytdlptool_relaxed', False):
            ssl.SSLContext.wrap_socket = _patched_wrap_socket
            if _original_wrap_bio is not None:
                ssl.SSLContext.wrap_bio = _patched_wrap_bio
            ssl.SSLContext._ytdlptool_relaxed = True
    except ImportError:
        pass
