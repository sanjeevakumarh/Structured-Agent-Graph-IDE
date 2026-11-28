"""Discover node identities and the gateway from YAML; roles drive dispatch."""
from pathlib import Path
import re

import yaml

CONFIG = Path(__file__).resolve().parents[1] / 'config'


def profiles(directory=CONFIG):
    result = {}
    for path in sorted(directory.glob('*.yaml')):
        data = yaml.safe_load(path.read_text())
        if not isinstance(data, dict) or 'node' not in data:
            continue
        name = data['node']
        if name != path.stem or not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_-]*', name):
            raise ValueError(f'Invalid node identity in {path.name}')
        if data.get('role') not in ('gateway', 'worker', 'edge'):
            raise ValueError(f'Unknown role in {path.name}')
        result[name] = data
    return result


def gateway(nodes):
    found = [name for name, spec in nodes.items() if spec['role'] == 'gateway']
    if len(found) != 1:
        raise ValueError('Expected exactly one gateway YAML profile')
    return found[0]


def routes_for(config, nodes):
    """Expand the active names using each node's API port. Accept older URL maps."""
    from urllib.parse import urlsplit
    active = config['nodes']
    if isinstance(active, dict):
        return active
    if not isinstance(active, list) or any(not isinstance(n, str) or n not in nodes for n in active):
        raise ValueError('nodes must list known node names')
    if len(set(active)) != len(active):
        raise ValueError('Duplicate gateway inventory entry')
    routes = {}
    for name in active:
        profile = nodes[name]
        if profile['role'] == 'gateway':
            url = profile['endpoint'].rstrip('/') + '/v1'
        else:
            port = profile.get('port') or urlsplit(profile['endpoint']).port
            url = f'http://{name}:{port}/v1'
        routes[name] = {'enabled': True, 'url': profile.get('gateway_url', url)}
    return routes


def members(nodes):
    for name, spec in routes_for(nodes[gateway(nodes)], nodes).items():
        if name not in nodes or type(spec.get('enabled')) is not bool:
            raise ValueError(f'Invalid gateway inventory entry: {name}')
        if spec['enabled']:
            yield name, nodes[name]['role']


if __name__ == '__main__':
    print(gateway(profiles()))
