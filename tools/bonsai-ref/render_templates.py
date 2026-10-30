#!/usr/bin/env python3
"""render_templates.py

Renders a model's own chat template for a fixed set of conversations, exactly the
way Hugging Face transformers renders chat templates (jinja2 sandbox, trim_blocks,
lstrip_blocks, loopcontrols, `tojson` with ensure_ascii=False, `raise_exception`).
The output is the reference the C# JinjaLite must reproduce byte for byte, and the
prompts `bonsai-ref logits` runs (cases marked "logits": true).

usage:  python -I render_templates.py <meta.json> <out.json>

<meta.json> is what `bonsai-ref meta` wrote: the template comes from the GGUF itself.
"""

import datetime
import json
import sys

from jinja2 import TemplateError
from jinja2.ext import loopcontrols
from jinja2.sandbox import ImmutableSandboxedEnvironment


def raise_exception(message):
    raise TemplateError(message)


def tojson(x, ensure_ascii=False, indent=None, separators=None, sort_keys=False):
    return json.dumps(x, ensure_ascii=ensure_ascii, indent=indent,
                      separators=separators, sort_keys=sort_keys)


def strftime_now(fmt):
    return datetime.datetime.now().strftime(fmt)


WEATHER_TOOL = {
    "type": "function",
    "function": {
        "name": "get_weather",
        "description": "Get the current weather for a place.",
        "parameters": {
            "type": "object",
            "properties": {"location": {"type": "string", "description": "City name"}},
            "required": ["location"],
        },
    },
}

LONG = ("Durban is a coastal city in KwaZulu-Natal, known for its warm Indian Ocean "
        "beaches, its harbour, and a large Indian community whose food, especially bunny "
        "chow, is famous across the country. The city hosts many festivals, and its "
        "botanic gardens are among the oldest in Africa. Somewhere in this paragraph the "
        "secret code is 7429, which you should remember. Durban's climate is humid and "
        "subtropical, with hot summers and mild winters. What is the secret code?")


def user(text):
    return {"role": "user", "content": text}


# (name, messages, tools, add_generation_prompt, enable_thinking, run logits)
CASES = [
    ("greet", [user("Hello! Who are you?")], None, True, False, True),
    ("fact", [user("What is the capital of Kenya?")], None, True, False, True),
    ("isizulu", [user("Sawubona! Ungangitshela ukuthi iyini inhloko-dolobha yaseNingizimu Afrika?")],
     None, True, False, True),
    ("afrikaans", [user("Wat is die hoofstad van Suid-Afrika?")], None, True, False, True),
    ("chinese", [user("请用一句话解释什么是人工智能。")], None, True, False, True),
    ("arabic", [user("ما هي عاصمة مصر؟")], None, True, False, True),
    ("code", [user("Write a Python function that checks whether a number is prime.")],
     None, True, False, True),
    ("math", [user("What is 17 times 23? Answer with just the number.")], None, True, False, True),
    ("system", [{"role": "system", "content": "You are a concise assistant."},
                user("Name three colours.")], None, True, False, True),
    ("multiturn", [user("My name is Thandi."),
                   {"role": "assistant", "content": "Nice to meet you, Thandi!"},
                   user("What is my name?")], None, True, False, True),
    ("tool", [user("What is the weather in Durban right now?")], [WEATHER_TOOL], True, False, True),
    ("long", [user(LONG)], None, True, False, True),
    # Template-only cases: what the renderer must reproduce, not run.
    ("thinking_on", [user("Hello!")], None, True, True, False),
    ("tool_roundtrip", [
        user("What is the weather in Durban?"),
        {"role": "assistant", "content": "",
         "tool_calls": [{"type": "function",
                         "function": {"name": "get_weather", "arguments": {"location": "Durban"}}}]},
        {"role": "tool", "content": "{\"temp_c\": 24, \"sky\": \"sunny\"}"},
    ], [WEATHER_TOOL], True, False, False),
    ("empty_arguments", [
        user("Check the weather."),
        {"role": "assistant", "content": "",
         "tool_calls": [{"type": "function", "function": {"name": "get_weather", "arguments": {}}}]},
    ], [WEATHER_TOOL], False, False, False),
    ("reasoning_history", [
        user("What is 2 + 2?"),
        {"role": "assistant", "content": "4", "reasoning_content": "Two plus two is four."},
        user("And 3 + 3?"),
    ], None, True, True, False),
    ("no_generation_prompt", [user("Hi"), {"role": "assistant", "content": "Hello!"}],
     None, False, False, False),
    ("two_system_messages", [{"role": "system", "content": "One."},
                             {"role": "system", "content": "Two."},
                             user("Hi")], None, True, False, False),

    # S2: what JinjaLite must get right beyond the everyday - content given as parts, several
    # tool calls, runs of tool results, reasoning written inline or kept apart, argument
    # values of every JSON type, numbers in a tool's schema, and every refusal the template
    # makes. Template-only: none of these runs logits.
    ("content_parts", [{"role": "user", "content": [{"type": "text", "text": "Describe "}, {"type": "image"},
                                                    {"type": "text", "text": " please."}]}], None, True, False, False),
    ("system_parts", [{"role": "system", "content": [{"type": "text", "text": "Be brief."}]}, user("Hi")],
     None, True, False, False),
    ("video_part", [{"role": "user", "content": [{"type": "video"}, {"text": "What happens?"}]}], None, True, False, False),
    ("image_url_part", [{"role": "user", "content": [{"image_url": "x.png"}, {"type": "text", "text": "And this?"}]}],
     None, True, False, False),
    ("two_tool_calls", [
        user("Weather in Durban and Cape Town?"),
        {"role": "assistant", "content": "Checking both.", "tool_calls": [
            {"type": "function", "function": {"name": "get_weather", "arguments": {"location": "Durban"}}},
            {"type": "function", "function": {"name": "get_weather", "arguments": {"location": "Cape Town"}}}]},
        {"role": "tool", "content": "{\"temp_c\": 24}"},
        {"role": "tool", "content": "{\"temp_c\": 19}"},
    ], [WEATHER_TOOL], True, False, False),
    ("tool_call_without_function_wrapper", [
        user("Weather?"),
        {"role": "assistant", "content": "", "tool_calls": [{"name": "get_weather", "arguments": {"location": "Durban"}}]},
        {"role": "tool", "content": "sunny"},
        user("Thanks. And tomorrow?"),
    ], [WEATHER_TOOL], True, True, False),
    ("argument_types", [
        user("Book it."),
        {"role": "assistant", "content": "", "tool_calls": [{"type": "function", "function": {"name": "book", "arguments": {
            "seats": 2, "price": 99.5, "tiny": 1e-07, "big": 1e16, "window": True, "note": None,
            "tags": ["a", "ñ", 3, 2.0], "who": {"name": "Thandi", "age": 30},
            "quote": "say \"hi\"\n\ttab", "unicode": "Ngiyabonga 😀"}}}]},
    ], [WEATHER_TOOL], False, False, False),
    ("tool_with_numbers", [user("Pick a number.")], [{
        "type": "function",
        "function": {"name": "pick", "description": "Pick one.", "parameters": {
            "type": "object", "properties": {"n": {"type": "number", "minimum": 0.5, "maximum": 1e16,
                                                   "default": 1e-05, "enum": [1, 2.0, -3.25]},
                                             "strict": {"type": "boolean", "default": False},
                                             "label": {"type": "string", "default": None}},
            "required": []}}}], True, False, False),
    ("inline_reasoning", [
        user("Q1"),
        {"role": "assistant", "content": "<think>\nfirst thought\n</think>\n\nA1"},
        user("Q2"),
        {"role": "assistant", "content": "<think>\nsecond\n</think>\n\nA2"},
    ], None, False, True, False),
    ("reasoning_after_last_query", [
        user("Q"),
        {"role": "assistant", "content": "partial", "reasoning_content": "  thinking...  "},
    ], None, False, True, False),
    ("multi_step_tool_query", [
        user("Find it."),
        {"role": "assistant", "content": "", "tool_calls": [
            {"type": "function", "function": {"name": "get_weather", "arguments": {"location": "Durban"}}}]},
        {"role": "user", "content": "<tool_response>\nsunny\n</tool_response>"},
    ], [WEATHER_TOOL], True, False, False),
    ("system_with_tools", [{"role": "system", "content": "Use tools wisely."}, user("Weather?")],
     [WEATHER_TOOL], True, False, False),
    ("blank_system_with_tools", [{"role": "system", "content": "   "}, user("Weather?")], [WEATHER_TOOL], True, False, False),
    ("tools_empty_list", [user("Hi")], [], True, False, False),
    ("none_content", [user("Hi"), {"role": "assistant", "content": None, "tool_calls": [
        {"type": "function", "function": {"name": "get_weather", "arguments": {"location": "X"}}}]}],
     [WEATHER_TOOL], False, False, False),
    ("whitespace_content", [user("  padded  \n")], None, True, False, False),
    ("unicode", [user("Ngiyabonga! 你好 مرحبا 😀 ́e")], None, True, False, False),
    ("no_messages", [], None, True, False, False),
    ("no_user_query", [{"role": "system", "content": "S"}, {"role": "assistant", "content": "A"}], None, True, False, False),
    ("unexpected_role", [user("Hi"), {"role": "narrator", "content": "..."}], None, True, False, False),
    ("image_in_system", [{"role": "system", "content": [{"type": "image"}]}, user("Hi")], None, True, False, False),
    ("unexpected_content_type", [user(42)], None, True, False, False),
    ("unexpected_item", [{"role": "user", "content": [{"type": "audio"}]}], None, True, False, False),
]


def main():
    meta_path, out_path = sys.argv[1], sys.argv[2]
    with open(meta_path, encoding="utf-8") as f:
        meta = json.load(f)
    template_src = meta["keys"]["tokenizer.chat_template"]

    env = ImmutableSandboxedEnvironment(trim_blocks=True, lstrip_blocks=True,
                                        extensions=[loopcontrols])
    env.filters["tojson"] = tojson
    env.globals["raise_exception"] = raise_exception
    env.globals["strftime_now"] = strftime_now
    template = env.from_string(template_src)

    cases = []
    for name, messages, tools, gen, thinking, logits in CASES:
        case = {"name": name, "messages": messages, "add_generation_prompt": gen,
                "enable_thinking": thinking, "logits": logits}
        if tools is not None:
            case["tools"] = tools
        try:
            case["rendered"] = template.render(messages=messages, tools=tools,
                                               add_generation_prompt=gen,
                                               enable_thinking=thinking)
        except TemplateError as e:
            # The template refuses some inputs on purpose; the C# renderer must too.
            case["error"] = str(e)
        cases.append(case)

    out = {
        "_comment": "The model's own chat template rendered the Hugging Face way (jinja2). "
                    "JinjaLite must reproduce `rendered` byte for byte and raise where `error` is set.",
        "_source": {"template_from": meta["_source"]["model"], "sha256": meta["_source"]["sha256"],
                    "renderer": "jinja2 " + __import__("jinja2").__version__,
                    "made": datetime.date.today().isoformat()},
        "cases": cases,
    }
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"wrote {out_path}: {len(cases)} cases, "
          f"{sum(1 for c in cases if 'error' in c)} refused by the template")


if __name__ == "__main__":
    main()
