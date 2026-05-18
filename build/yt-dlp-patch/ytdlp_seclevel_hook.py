# yt-dlp PyInstaller runtime hook
#
# Activated only when the environment variable YTDLP_RELAX_SECLEVEL=1 is set.
# Lowers OpenSSL's per-context security level to 0 right before each TLS
# handshake, so connections complete on networks whose HTTPS inspection
# products present leaf certificates with key sizes that the default
# SECLEVEL=1 would reject.
#
# Certificate chain validation and hostname verification still run; only the
# key-strength gate at handshake time is relaxed.
#
# When the env var is unset, this hook is a no-op and yt-dlp behaves exactly
# like upstream.
#
# v1.3.0-alpha2: patches wrap_socket / wrap_bio instead of __init__. The
# alpha1 approach replaced ssl.SSLContext.__init__ with a wrapper, which
# broke MRO for SSLContext subclasses that call super().__init__(*args,
# **kwargs) — the args propagated all the way up to object.__init__ and
# raised "TypeError: object.__init__() takes exactly one argument". Patching
# wrap_socket / wrap_bio instead avoids touching the constructor entirely.
# These methods are called right before each TLS handshake, so applying
# set_ciphers('DEFAULT@SECLEVEL=0') here has the same effect with no
# inheritance side effects.
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

        def _ensure_seclevel_0(ctx):
            try:
                _original_set_ciphers(ctx, 'DEFAULT@SECLEVEL=0')
            except ssl.SSLError:
                pass

        def _patched_wrap_socket(self, *args, **kwargs):
            _ensure_seclevel_0(self)
            return _original_wrap_socket(self, *args, **kwargs)

        def _patched_wrap_bio(self, *args, **kwargs):
            _ensure_seclevel_0(self)
            return _original_wrap_bio(self, *args, **kwargs)

        ssl.SSLContext.wrap_socket = _patched_wrap_socket
        if _original_wrap_bio is not None:
            ssl.SSLContext.wrap_bio = _patched_wrap_bio
    except ImportError:
        pass
