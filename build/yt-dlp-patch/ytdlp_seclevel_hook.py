# yt-dlp PyInstaller runtime hook
#
# Activated only when the environment variable YTDLP_RELAX_SECLEVEL=1 is set.
# Lowers OpenSSL's per-context security level to 0 on every ssl.SSLContext
# created within yt-dlp's bundled Python, so TLS handshakes complete on
# networks whose HTTPS inspection products present leaf certificates with
# key sizes that the default SECLEVEL=1 would reject.
#
# Certificate chain validation and hostname verification still run; only the
# key-strength gate at handshake time is relaxed.
#
# When the env var is unset, this hook is a no-op and yt-dlp behaves exactly
# like upstream.
#
# Source location:  build/yt-dlp-patch/ytdlp_seclevel_hook.py
# Repo:             https://github.com/AwesomeBigBoy/YTDownloadTool

import os

if os.environ.get('YTDLP_RELAX_SECLEVEL') == '1':
    try:
        import ssl
        _original_init = ssl.SSLContext.__init__

        def _patched_init(self, *args, **kwargs):
            _original_init(self, *args, **kwargs)
            try:
                self.set_ciphers('DEFAULT@SECLEVEL=0')
            except ssl.SSLError:
                pass

        ssl.SSLContext.__init__ = _patched_init
    except ImportError:
        pass
