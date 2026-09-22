"""A correctly registered weather MCP backend for isolated Router acceptance.

The default fixture is deterministic. --live-weather reads Open-Meteo for Oslo;
fixture observations are explicitly labelled and never reported as live weather.
"""
import json
import sys
import urllib.request

from mcp.server.fastmcp import FastMCP

server = FastMCP("weather-mcp")


@server.tool()
def get_weather(city: str) -> str:
    """Return the temperature in Celsius for the acceptance city, Oslo."""
    if city != "Oslo":
        raise ValueError("This acceptance backend supports Oslo only")
    if "--live-weather" in sys.argv:
        url = "https://api.open-meteo.com/v1/forecast?latitude=59.91&longitude=10.75&current=temperature_2m"
        with urllib.request.urlopen(url, timeout=15) as response:
            observation = json.load(response)["current"]
        result = {"city": city, "temperature_c": observation["temperature_2m"],
                  "time": observation["time"], "source": "Open-Meteo"}
    else:
        result = {"city": city, "temperature_c": 12.5, "source": "acceptance fixture"}
    return json.dumps(result)


if __name__ == "__main__":
    server.run(transport="stdio")
