"""Run the unmodified pinned Router protocol on loopback for acceptance."""
import uvicorn
from nacos_mcp_router.router import main

run = uvicorn.run


def local_run(app, **kwargs):
    kwargs["host"] = "127.0.0.1"
    return run(app, **kwargs)


uvicorn.run = local_run
main()
