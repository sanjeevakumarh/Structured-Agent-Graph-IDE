"""Role-based identities for tests that exercise the shipped example profiles."""
from inventory import gateway, profiles

NODES = profiles()
GATEWAY = gateway(NODES)
AMD = next(n for n, p in NODES.items() if p.get('hardware', {}).get('vendor') == '0x1002')
CUDA = next(n for n, p in NODES.items() if p.get('media_backend') == 'cuda')
XPU = next(n for n, p in NODES.items() if p.get('media_backend') == 'xpu')
OLLAMA = next(n for n, p in NODES.items() if p.get('runtime') == 'ollama')
GENIEX = next(n for n, p in NODES.items() if p.get('runtime') == 'geniex')
