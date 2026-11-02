#!/usr/bin/env python3
"""Copy legacy temporary demo state to a durable, git-ignored directory once.

Run only after checking the demo port is stopped. Never overwrite or delete a
source or existing destination. A staged copy is renamed only when complete.
"""
from pathlib import Path
import os
import shutil
import sys
import tempfile


def migrate(source, destination):
    source, destination = Path(source), Path(destination)
    if destination.exists() or not source.is_dir():
        return False
    if not (source / 'audit.log.config/active.json').is_file():
        return False
    destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    stage = Path(tempfile.mkdtemp(prefix='.migration-', dir=destination.parent))
    try:
        shutil.copytree(source, stage, dirs_exist_ok=True, symlinks=True)
        os.chmod(stage, 0o700)
        os.rename(stage, destination)
    except BaseException:
        shutil.rmtree(stage)
        raise
    return True


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit('Usage: demo-state.py <legacy-state> <durable-state>')
    if migrate(sys.argv[1], sys.argv[2]):
        print('Saved demo state copied to durable storage; the legacy copy is retained.')
