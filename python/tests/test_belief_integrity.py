"""test_belief_integrity.py

Verifies the memory-integrity core: attribution discipline (self/other/world),
and SelfBeliefStore filtering, revision (supersede), correction (retract), and
provenance. The headline guarantee: "my mother is diabetic" never becomes a fact
about the user. Mirrors the TypeScript pilot (belief_integrity.test.ts) and Go
port.
"""
from __future__ import annotations

from datetime import datetime, timezone

from circle_ai.companion.belief import (
    Attribution,
    HeuristicBeliefExtractor,
    PersonalBelief,
    SelfBeliefStore,
)

ex = HeuristicBeliefExtractor()


async def _one(text: str) -> PersonalBelief:
    beliefs = await ex.extract_async(text, "turn")
    assert len(beliefs) == 1, f'expected one belief from "{text}"'
    return beliefs[0]


# ── attribution ───────────────────────────────────────────────────────────────


async def test_my_mother_is_diabetic_is_other_about_the_mother() -> None:
    b = await _one("my mother is diabetic")
    assert b.attribution is Attribution.Other
    assert b.subject == "mother"
    assert b.object == "diabetic"


async def test_i_am_vegetarian_is_self_about_the_user() -> None:
    b = await _one("i am vegetarian")
    assert b.attribution is Attribution.Self
    assert b.subject == "user"
    assert b.object == "vegetarian"


async def test_my_car_is_fast_my_plus_non_relation_is_self() -> None:
    b = await _one("my car is fast")
    assert b.attribution is Attribution.Self
    assert b.subject == "user"


async def test_a_bare_relation_as_subject_is_other() -> None:
    b = await _one("brother lives in Cape Town")
    assert b.attribution is Attribution.Other
    assert b.subject == "brother"


async def test_a_general_statement_is_world() -> None:
    b = await _one("paris is beautiful")
    assert b.attribution is Attribution.World
    assert b.subject == "paris"


# ── SelfBeliefStore — filtering, revision, correction ─────────────────────────


async def test_only_self_beliefs_become_user_facts() -> None:
    store = SelfBeliefStore()
    for b in await ex.extract_async("my mother is diabetic", "t1"):
        store.record(b)
    for b in await ex.extract_async("i am vegetarian", "t2"):
        store.record(b)

    facts = store.self_facts()
    assert len(facts) == 1
    assert facts[0].object == "vegetarian"

    # The mother's fact is remembered, but never as a user fact.
    assert not any("diabetic" in f.object for f in facts)
    assert any(b.object == "diabetic" for b in store.non_self())


def test_a_newer_self_belief_supersedes_the_older_on_same_predicate() -> None:
    store = SelfBeliefStore()

    def mk(obj: str) -> PersonalBelief:
        return PersonalBelief(
            attribution=Attribution.Self,
            subject="user",
            predicate="isAbout",
            object=obj,
            confidence=0.6,
            source="t",
            recorded_at_utc=datetime.now(timezone.utc),
        )

    store.record(mk("vegetarian"))
    store.record(mk("vegan"))

    facts = store.self_facts()
    assert len(facts) == 1
    assert facts[0].object == "vegan"


async def test_retract_removes_user_facts_mentioning_the_text() -> None:
    store = SelfBeliefStore()
    for b in await ex.extract_async("i am vegetarian", "t1"):
        store.record(b)
    removed = store.retract("vegetarian")
    assert removed == 1
    assert len(store.self_facts()) == 0


def test_provenance_returns_distinct_source_turns() -> None:
    # Distinct predicates so both survive — the heuristic extractor always uses
    # "isAbout", which would (correctly) supersede one self-fact with the next.
    store = SelfBeliefStore()

    def mk(obj: str, predicate: str, source: str) -> PersonalBelief:
        return PersonalBelief(
            attribution=Attribution.Self,
            subject="user",
            predicate=predicate,
            object=obj,
            confidence=0.6,
            source=source,
            recorded_at_utc=datetime.now(timezone.utc),
        )

    store.record(mk("vegetarian", "diet", "t1"))
    store.record(mk("hiking", "hobby", "t2"))
    prov = sorted(store.provenance())
    assert prov == ["t1", "t2"]
