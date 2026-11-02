#!/usr/bin/env python3
"""Load only the demo's saved Anthropic/Tavily credentials, then exec the Host.

Explicit environment values win. LOAD_SAVED_KEYS=0 disables the local store.
No credential is printed, written, or passed as a command-line argument here.
"""
import json
import os
from pathlib import Path
import sys


def host_environment(source, secret_file):
    env = dict(source)
    keys = (('Anthropic:ApiKey', 'Anthropic__ApiKey'), ('Tavily:ApiKey', 'Tavily__ApiKey'))
    if env.get('LOAD_SAVED_KEYS', '1') != '0' and any(name not in env for _, name in keys):
        try:
            saved = json.loads(secret_file.read_text(encoding='utf-8-sig'))
        except FileNotFoundError:
            saved = {}
        except (OSError, ValueError):
            raise ValueError('Cannot read Covenant saved keys; check secrets.json or use LOAD_SAVED_KEYS=0.') from None
        if not isinstance(saved, dict):
            raise ValueError('Covenant secrets.json must contain a JSON object.')
        for key, name in keys:
            value = saved.get(key)
            if name not in env and isinstance(value, str) and value.strip():
                env[name] = value
    # Keep an explicitly supplied allow-list exactly as supplied. These fixed provider
    # destinations are defaults for the demo only; runtime policy still gates every call.
    if 'Providers__ProbeAllowList' not in env:
        hosts = [env.get('Providers__LocalEndpoints', 'localhost:11434')]
        for key, host in [('Anthropic__ApiKey', 'api.anthropic.com:443'), ('Tavily__ApiKey', 'api.tavily.com:443')]:
            if env.get(key, '').strip():
                hosts.append(host)
        env['Providers__ProbeAllowList'] = ','.join(hosts)
    return env


def main():
    if len(sys.argv) != 2:
        sys.exit('Usage: demo-credentials.py <Covenant.Host>')
    path = Path.home() / '.microsoft/usersecrets/covenant-host/secrets.json'
    try:
        env = host_environment(os.environ, path)
    except ValueError as error:
        sys.exit(str(error))
    os.execve(sys.argv[1], [sys.argv[1]], env)


if __name__ == '__main__':
    main()
