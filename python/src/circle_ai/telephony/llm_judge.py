# llm_judge.py
#
# Port of CircleAI.Telephony LlmJudge.cs (C# — the EXACT spec).
#
# (3.3.0) LLM-as-judge: an LLM scores another LLM's reply against a rubric. Used
# in EvalSession to grade responses on dimensions like "policy compliance",
# "tone match", "factual accuracy".
#
# C# delegate JudgeCompletion (Task<string>(string, CancellationToken)) -> an
# async Callable. C# System.Text.Json parsing -> json.loads over the extracted
# object. Score coercion mirrors the C# switch: JSON number -> int(truncate),
# numeric string -> int, anything else -> 0; a missing dimension -> 0. Any parse
# failure yields the all-zero borderline verdict, exactly like the C# catch.

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Awaitable, Callable, Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class JudgeDimension:
    """(3.3.0) One scoring dimension.

    ``name``: display name. ``description``: plain-English rubric the judge sees.
    """

    name: str
    description: str


@dataclass(frozen=True, slots=True)
class JudgeVerdict:
    """(3.3.0) Result of one judging call.

    ``scores``: 0..10 per dimension. ``overall``: pass / borderline / fail.
    ``reasoning``: one paragraph.
    """

    scores: Dict[str, int]
    overall: str
    reasoning: str


# (3.3.0) Delegate that asks the actual LLM to grade.
JudgeCompletion = Callable[[str, Optional[object]], Awaitable[str]]


class LlmJudge:
    """(3.3.0) LLM-as-judge driver."""

    def __init__(self, completion: JudgeCompletion) -> None:
        if completion is None:
            raise ValueError("completion must not be None")
        self._completion = completion

    async def judge_async(
        self,
        user_utterance: str,
        assistant_response: str,
        dimensions: Sequence[JudgeDimension],
        *,
        ct: Optional[object] = None,
    ) -> JudgeVerdict:
        """(3.3.0) Build the rubric prompt, ask the judge, parse JSON, return the verdict."""
        if user_utterance is None:
            raise ValueError("user_utterance must not be None")
        if assistant_response is None:
            raise ValueError("assistant_response must not be None")
        if dimensions is None:
            raise ValueError("dimensions must not be None")

        prompt = self._build_prompt(user_utterance, assistant_response, dimensions)
        raw = await self._completion(prompt, ct)
        return self._parse_verdict(raw, dimensions)

    @staticmethod
    def _build_prompt(user: str, assistant: str, dims: Sequence[JudgeDimension]) -> str:
        lines: List[str] = []
        lines.append("You are an evaluation judge. Score the assistant's reply across the rubric below.")
        lines.append("Reply ONLY in this JSON shape:")
        lines.append(
            '{ "scores": { "<dim_name>": <0-10>, ... }, "overall": "pass|borderline|fail", '
            '"reasoning": "<one paragraph>" }'
        )
        lines.append("")
        lines.append("Rubric:")
        for d in dims:
            lines.append(f"- {d.name}: {d.description}")
        lines.append("")
        lines.append("User utterance:")
        lines.append(user)
        lines.append("")
        lines.append("Assistant reply:")
        lines.append(assistant)
        # C# StringBuilder.AppendLine terminates every line (including the last).
        return "\n".join(lines) + "\n"

    @staticmethod
    def _parse_verdict(raw: str, dims: Sequence[JudgeDimension]) -> JudgeVerdict:
        scores: Dict[str, int] = {}
        try:
            trimmed = LlmJudge._extract_json(raw)
            root = json.loads(trimmed)
            if not isinstance(root, dict):
                raise ValueError("root not an object")
            s = root.get("scores")
            if isinstance(s, dict):
                for dim in dims:
                    if dim.name in s:
                        v = s[dim.name]
                        scores[dim.name] = LlmJudge._coerce_score(v)
                    else:
                        scores[dim.name] = 0
            overall = root.get("overall")
            overall = overall if isinstance(overall, str) else "borderline"
            reason = root.get("reasoning")
            reason = reason if isinstance(reason, str) else ""
            return JudgeVerdict(scores, overall, reason)
        except Exception:
            for d in dims:
                scores[d.name] = 0
            return JudgeVerdict(scores, "borderline", "Judge response could not be parsed.")

    @staticmethod
    def _coerce_score(v: object) -> int:
        # C#: JSON number -> GetInt32 (truncates toward zero); numeric string ->
        # int.TryParse; anything else (incl. bool) -> 0.
        if isinstance(v, bool):
            return 0
        if isinstance(v, int):
            return v
        if isinstance(v, float):
            return int(v)
        if isinstance(v, str):
            try:
                return int(v.strip())
            except ValueError:
                return 0
        return 0

    @staticmethod
    def _extract_json(raw: str) -> str:
        """(3.3.0) Tolerate models that wrap JSON in prose or fenced code blocks."""
        start = raw.find("{")
        end = raw.rfind("}")
        if start < 0 or end < 0 or end <= start:
            return raw
        return raw[start : end + 1]
