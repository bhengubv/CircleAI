"""test_integration_home_assistant.py

Verifies the CircleAI.Integration.HomeAssistant port: entity listing with
attribute coercion + domain/friendly-name derivation, service calls with URL
escaping, and turn_on/turn_off convenience helpers. C# is the spec.
"""
from __future__ import annotations

import json

import pytest

from circle_ai.integration import InMemoryHttpFetcher, HttpResponse
from circle_ai.integration_home_assistant import (
    HomeAssistantConnector,
    HomeAssistantOptions,
)


def _states_fetcher(states: list) -> InMemoryHttpFetcher:
    f = InMemoryHttpFetcher()
    f.on_url_contains("api/states", HttpResponse(200, json.dumps(states)))
    # Any service POST returns 200.
    f.on_url_contains("api/services/", HttpResponse(200, "[]"), method="POST")
    return f


def _connector(f: InMemoryHttpFetcher, token: str = "tok") -> HomeAssistantConnector:
    return HomeAssistantConnector(
        HomeAssistantOptions("http://ha.local:8123/", token), f
    )


async def test_is_configured() -> None:
    f = InMemoryHttpFetcher()
    assert _connector(f).is_configured is True
    assert _connector(f, "  ").is_configured is False
    assert (
        HomeAssistantConnector(HomeAssistantOptions("", "tok"), f).is_configured
        is False
    )


async def test_provider_id() -> None:
    assert _connector(InMemoryHttpFetcher()).provider_id == "home-assistant"


async def test_list_entities_coerces_attributes_and_derives_domain() -> None:
    states = [
        {
            "entity_id": "light.kitchen",
            "state": "on",
            "attributes": {
                "friendly_name": "Kitchen Light",
                "brightness": 254,
                "temp": 21.5,
                "is_dimmable": True,
                "disabled": False,
                "rgb_color": [255, 0, 0],
            },
        },
        {"entity_id": "", "state": "ignored"},  # blank id skipped
        {"state": "no id"},  # missing id skipped
    ]
    conn = _connector(_states_fetcher(states))
    ents = await conn.list_entities_async()
    assert len(ents) == 1
    e = ents[0]
    assert e.entity_id == "light.kitchen"
    assert e.domain == "light"
    assert e.friendly_name == "Kitchen Light"
    assert e.state == "on"
    assert e.attributes["brightness"] == "254"
    assert e.attributes["temp"] == "21.5"
    assert e.attributes["is_dimmable"] == "true"
    assert e.attributes["disabled"] == "false"
    assert e.attributes["rgb_color"] == "[255,0,0]"


async def test_list_entities_friendly_name_defaults_to_entity_id() -> None:
    states = [{"entity_id": "sensor.temp", "state": "20", "attributes": {}}]
    conn = _connector(_states_fetcher(states))
    ents = await conn.list_entities_async()
    assert ents[0].friendly_name == "sensor.temp"


async def test_list_entities_non_array_returns_empty() -> None:
    f = InMemoryHttpFetcher()
    f.on_url_contains("api/states", HttpResponse(200, json.dumps({"not": "array"})))
    conn = _connector(f)
    assert await conn.list_entities_async() == []


async def test_call_service_escapes_and_posts_payload() -> None:
    f = _states_fetcher([])
    conn = _connector(f)
    await conn.call_service_async("light", "turn_on", {"entity_id": "light.kitchen"})
    req = f.last_request
    assert req.method == "POST"
    assert req.url.endswith("api/services/light/turn_on")
    assert req.body_json == {"entity_id": "light.kitchen"}
    assert req.headers["Authorization"] == "Bearer tok"


async def test_call_service_requires_domain_and_service() -> None:
    conn = _connector(_states_fetcher([]))
    with pytest.raises(ValueError):
        await conn.call_service_async("", "svc", None)
    with pytest.raises(ValueError):
        await conn.call_service_async("dom", "  ", None)


async def test_call_service_none_data_posts_empty_object() -> None:
    f = _states_fetcher([])
    conn = _connector(f)
    await conn.call_service_async("homeassistant", "restart", None)
    assert f.last_request.body_json == {}


async def test_turn_on_off_use_homeassistant_services() -> None:
    f = _states_fetcher([])
    conn = _connector(f)
    await conn.turn_on_async("light.kitchen")
    assert f.last_request.url.endswith("api/services/homeassistant/turn_on")
    assert f.last_request.body_json == {"entity_id": "light.kitchen"}
    await conn.turn_off_async("light.kitchen")
    assert f.last_request.url.endswith("api/services/homeassistant/turn_off")


async def test_list_entities_raises_on_http_error() -> None:
    f = InMemoryHttpFetcher()
    f.on_url_contains("api/states", HttpResponse(500, ""))
    conn = _connector(f)
    from circle_ai.integration import HttpError

    with pytest.raises(HttpError):
        await conn.list_entities_async()
