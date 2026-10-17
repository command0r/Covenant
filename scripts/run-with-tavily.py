#!/usr/bin/env python3
"""Run the demo with Tavily from env or Covenant's local .NET user-secret store.

Loads only Tavily:ApiKey, not other saved credentials. Runtime policy is unchanged:
select Hybrid, enable search and grant it to a Public profile in the console.
"""
import json
import os
from pathlib import Path
import sys


def main():
    env = os.environ.copy()
    if not env.get('Tavily__ApiKey', '').strip():
        if os.name == 'nt' and env.get('APPDATA'):
            secret_file = Path(env['APPDATA']) / 'Microsoft/UserSecrets/covenant-host/secrets.json'
        else:
            secret_file = Path.home() / '.microsoft/usersecrets/covenant-host/secrets.json'
        try:
            secrets = json.loads(secret_file.read_text(encoding='utf-8-sig'))
            key = secrets.get('Tavily:ApiKey')
        except (OSError, ValueError):
            sys.exit('Cannot read Covenant user secrets. Set Tavily__ApiKey or save Tavily:ApiKey locally first.')
        if not isinstance(key, str) or not key.strip():
            sys.exit('Tavily:ApiKey is missing. Save it locally first; do not put it in this script.')
        env['Tavily__ApiKey'] = key
    # Explicit invocation approves only this additional search destination.
    allowed = env.get('Providers__ProbeAllowList', '')
    env['Providers__ProbeAllowList'] = allowed + ',api.tavily.com:443'
    launcher = Path(__file__).resolve().with_name('run-local-demo.sh')
    os.execve('/bin/bash', ['bash', str(launcher)], env)


if __name__ == '__main__':
    main()
