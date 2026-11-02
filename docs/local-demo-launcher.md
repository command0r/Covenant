# Running the local demo

Run `scripts/run-local-demo.sh` from the repository. It rebuilds Covenant, opens the console, and resumes the saved configuration. Stop it with Ctrl+C before launching it again.

Configuration, audit history and budget accounting now live in `.covenant-local/demo-local-only/` (Git-ignored), not `/tmp`. The directory name identifies the original startup profile; the **saved configuration** determines whether the running service is Local-only or Hybrid. Changing routes or mode in the console persists across restarts. The launcher copies an existing legacy `/tmp/covenant-v2-demo-local-only/` directory once, retaining the original copy.

`PROFILE=hybrid` selects a separate state directory; it is not a command to change the current configuration. `STATE=/your/private/path` explicitly selects another state directory. `FRESH=1` archives the selected state directory and creates a new seed; do not use it for a normal restart.

Saved Anthropic and Tavily credentials are loaded from the `covenant-host` user-secrets store; explicit environment variables take precedence. Credentials being available does not enable cloud calls: an approved connection, Hybrid mode and a matching route are still required. The console's connection URL is private and regenerated on each launch.

The published binary can remain in `/tmp`: it is replaceable. The saved state is not. Back up `.covenant-local/` privately if the demo history matters; do not commit or share it.
