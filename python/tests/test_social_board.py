"""test_social_board.py — CircleAI.Social port.

Covers InMemorySocialBoard (posts, reaction count case-insensitive, follow graph
incl. self-follow guard + unfollow, feed from followees, followers) and
SocialDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    Follow,
    ISocialBoard,
    InMemorySocialBoard,
    Reaction,
    SocialDomainContext,
    SocialPost,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def test_board_is_isocialboard():
    assert isinstance(InMemorySocialBoard(), ISocialBoard)


def test_reaction_count_case_insensitive():
    b = InMemorySocialBoard()
    b.react(Reaction("p1", "u1", "Like", _at(0)))
    b.react(Reaction("p1", "u2", "like", _at(1)))
    b.react(Reaction("p1", "u3", "love", _at(2)))
    assert b.reaction_count("p1", "LIKE") == 2


def test_follow_self_raises():
    with pytest.raises(RuntimeError):
        InMemorySocialBoard().follow(Follow("u1", "u1", _at(0)))


def test_feed_from_followees_newest_first():
    b = InMemorySocialBoard()
    b.follow(Follow("me", "a", _at(0)))
    b.post(SocialPost("p1", "a", "hi", _at(5), []))
    b.post(SocialPost("p2", "a", "later", _at(10), []))
    b.post(SocialPost("p3", "stranger", "ignored", _at(20), []))
    feed = b.feed_for("me")
    assert [p.post_id for p in feed] == ["p2", "p1"]


def test_unfollow_removes_from_feed():
    b = InMemorySocialBoard()
    b.follow(Follow("me", "a", _at(0)))
    b.post(SocialPost("p1", "a", "hi", _at(5), []))
    b.unfollow("me", "a")
    assert b.feed_for("me") == []


def test_followers_lists_follower_ids():
    b = InMemorySocialBoard()
    b.follow(Follow("u1", "star", _at(0)))
    b.follow(Follow("u2", "star", _at(1)))
    assert set(b.followers("star")) == {"u1", "u2"}


def test_feed_limit_zero_raises():
    with pytest.raises(ValueError):
        InMemorySocialBoard().feed_for("me", limit=0)


def test_social_domain_context():
    assert SocialDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Social]")
    assert "ASA_Advertising_Code" in SocialDomainContext.ComplianceFlags
