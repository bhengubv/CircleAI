#!/usr/bin/env python3
"""render_snippets.py

Renders small templates that each pin one jinja2 behaviour JinjaLite must share - whitespace
control, loops, scoping, macros, Python's numbers and reprs, string methods, tests, errors -
in the same environment Hugging Face renders chat templates with (and render_templates.py
uses). The output is the ruler for JinjaLite's general behaviour, as templates.json is for the
2B's own template.

usage:  python -I render_snippets.py <out.json>
"""

import datetime
import json
import sys

import jinja2
from jinja2 import TemplateError
from jinja2.ext import loopcontrols
from jinja2.sandbox import ImmutableSandboxedEnvironment


def raise_exception(message):
    raise TemplateError(message)


def tojson(x, ensure_ascii=False, indent=None, separators=None, sort_keys=False):
    return json.dumps(x, ensure_ascii=ensure_ascii, indent=indent,
                      separators=separators, sort_keys=sort_keys)


# (name, template, variables)
SNIPPETS = [
    # whitespace: trim_blocks, lstrip_blocks, - and +, comments, newlines
    ("trim_and_lstrip", "{% if true %}\n  yes\n{% endif %}\nafter", {}),
    ("indented_block", "line1\n    {% if true %}\n    inner\n    {% endif %}\nline2", {}),
    ("lstrip_not_for_variables", "  {{ x }}  \n  {% if true %}b{% endif %}", {"x": "a"}),
    ("minus_both_sides", "a  \n {%- if true -%} \n b \n {%- endif -%}  \n c", {}),
    ("plus_keeps_indent", "a\n  {%+ if true %}b{% endif %}", {}),
    ("comment_lines", "a\n{# a comment #}\nb\n  {# indented #}\nc\n{#- stripped -#}\n  d", {}),
    ("variable_minus", "x {{- ' y ' -}} z", {}),
    ("for_lines", "{% for x in [1, 2] %}\n  {{ x }}\n{% endfor %}\nend", {}),
    ("windows_newlines", "a\r\nb\rc\r\n", {}),
    ("one_trailing_newline_dropped", "text\n\n", {}),
    ("text_only", "plain text, no tags", {}),
    # loops
    ("loop_variables", "{% for x in 'abc' %}{{ loop.index }}{{ loop.index0 }}{{ loop.revindex }}{{ loop.revindex0 }}"
                       "{{ loop.first }}{{ loop.last }}{{ loop.length }};{% endfor %}", {}),
    ("loop_neighbours", "{% for x in [1, 2, 3] %}[{{ loop.previtem }}<{{ x }}>{{ loop.nextitem }}]{% endfor %}", {}),
    ("for_else", "{% for x in [] %}a{% else %}empty{% endfor %}|{% for x in none %}a{% else %}none{% endfor %}", {}),
    ("for_filter", "{% for x in [1, 2, 3, 4] if x > 1 %}{{ x }}/{{ loop.length }} {% endfor %}", {}),
    ("break_continue", "{% for x in [1, 2, 3, 4, 5] %}{% if x == 2 %}{% continue %}{% endif %}"
                       "{% if x == 4 %}{% break %}{% endif %}{{ x }}{% endfor %}", {}),
    ("tuple_targets", "{% for k, v in d | items %}{{ k }}={{ v }};{% endfor %}{% for a, b in [[1, 2], [3, 4]] %}{{ a + b }}{% endfor %}",
     {"d": {"x": 1, "y": [2]}}),
    ("nested_loops", "{% for a in [1, 2] %}{% for b in [1, 2] %}{{ loop.index }}{% endfor %}{{ loop.index }}|{% endfor %}", {}),
    ("dict_iteration", "{% for k in d %}{{ k }}{% endfor %}", {"d": {"b": 1, "a": 2}}),
    # scoping
    ("loop_scope", "{% set x = 1 %}{% for i in [1] %}{% set x = 2 %}{{ x }}{% endfor %}{{ x }}", {}),
    ("namespace", "{% set ns = namespace(n=0, s='') %}{% for x in [1, 2, 3] %}{% set ns.n = ns.n + x %}"
                  "{% set ns.s = ns.s ~ x %}{% endfor %}{{ ns.n }} {{ ns.s }}", {}),
    ("if_does_not_scope", "{% if true %}{% set x = 'set in if' %}{% endif %}{{ x }}", {}),
    ("set_block", "{% set block %}hello {{ name }}{% endset %}[{{ block }}]", {"name": "Thandi"}),
    ("set_tuple", "{% set a, b = 1, 2 %}{{ a }}{{ b }}", {}),
    # macros
    ("macro_defaults", "{% macro m(a, b=2, c='x') %}{{ a }}-{{ b }}-{{ c }}{% endmacro %}{{ m(1) }}|{{ m(1, 3) }}|{{ m(1, c='y') }}|{{ m() }}", {}),
    ("macro_sees_top_level", "{% set top = 'T' %}{% macro m() %}{{ top }}{{ local }}{% endmacro %}"
                             "{% for local in [1] %}{{ m() }}{% endfor %}", {}),
    ("macro_value_is_text", "{% macro m(x) %} {{ x }} {% endmacro %}[{{ m(1) | trim }}]{{ m(2) | length }}", {}),
    # expressions
    ("conditional", "{{ 'yes' if x else 'no' }}{{ 'a' if false }}|{{ 1 if true else 2 if true else 3 }}", {}),
    ("and_or_values", "{{ 0 or 'b' }} {{ 'a' and 'b' }} {{ '' and 'b' }}| {{ none or [] or 'c' }} {{ not 0 }}", {}),
    ("arithmetic", "{{ 0.1 + 0.2 }} {{ 10 / 4 }} {{ 10 // 4 }} {{ -7 // 2 }} {{ -7 % 3 }} {{ 7.5 % 2 }} {{ 2 ** 10 }} {{ 2 ** -1 }} {{ -(3) }} {{ 1 - -1 }}", {}),
    ("floats", "{{ 1e16 }} {{ 1e15 }} {{ 1e-5 }} {{ 0.0001 }} {{ 2.5e-4 }} {{ 1.0 }} {{ 123.456 }} {{ -0.0 }} {{ 3.0 * 2 }} {{ 1 / 3 }}", {}),
    ("precedence", "{{ 1 + 2 * 3 }} {{ (1 + 2) * 3 }} {{ 2 ** 3 ** 2 }} {{ 'a' ~ (1 + 2) }} {{ [1, 2] | length - 1 }} {{ not true == false }} {{ -2 ** 2 }}", {}),
    ("tilde_binds_tighter_than_plus", "{{ 'a' ~ 1 + 2 }}", {}),
    ("concat", "{{ x ~ 1 ~ none ~ true ~ 2.0 }}|{{ 'ab' * 3 }}|{{ [1] + [2] }}", {}),
    ("reprs", "{{ [1, 'a', none, true, 2.5] }} {{ {'k': 'v', 'q': \"it's\"} }} {{ (1, 2) }} {{ (1,) }} {{ d.items() | list }}",
     {"d": {"a": 1}}),
    ("string_repr_escapes", "{{ ['a\\nb', 'tab\\t', \"q'\", 'both \\' \"', 'back\\\\slash', 'é ✓'] }}", {}),
    ("subscripts", "{{ 'abc'[1] }}{{ 'abcdef'[1:4] }}{{ 'abc'[::-1] }}{{ [1, 2, 3][-1] }}[{{ [1, 2, 3][5] }}]{{ [1, 2, 3, 4][::2] }}{{ 'héllo😀'[1:3] }}", {}),
    ("lengths", "{{ 'héllo😀' | length }} {{ [1, 2] | length }} {{ {'a': 1} | length }} {{ x | length }}", {}),
    ("membership", "{{ 'b' in 'abc' }} {{ 2 in [1, 2] }} {{ 'k' in {'k': 1} }} {{ 'z' not in 'abc' }} {{ 1 in [1.0] }} {{ 'x' in u }}", {}),
    ("comparisons", "{{ 1 < 2 < 3 }} {{ 3 > 2 > 2 }} {{ 1 == 1.0 }} {{ true == 1 }} {{ 'b' > 'a' }} {{ [1, 2] < [1, 3] }} {{ none == none }} {{ x == y }}", {}),
    # d.items is the method, not the key (printing it would show an address); d['items'] is the key.
    ("attribute_vs_item", "{{ d.a }} {{ d['a'] }} {{ d['items'] }} {{ d.items is defined }} {{ d.missing }}|{{ d.get('a') }} {{ d.get('z', 'dflt') }} {{ l.append }}",
     {"d": {"a": 1, "items": "key"}, "l": [1]}),
    ("literals", "{{ [1, [2, 3], {'a': 'b'}] }} {{ {'x': none, 'y': true} | tojson }} {{ 'adj' \"acent\" }} {{ True }} {{ None }}", {}),
    ("escapes", "{{ 'a\\tb\\\\c\\x41\\u00e9\\U0001F600\\101\\q' }}", {}),
    # filters
    ("trim", "[{{ '  a b  ' | trim }}][{{ 'xxaxx' | trim('x') }}][{{ x | trim }}][{{ 5 | trim }}]", {}),
    ("default", "{{ x | default('d') }} {{ none | default('d') }} {{ '' | default('d', true) }} {{ 0 | d(1) }}", {}),
    ("tojson", "{{ [1, 2.0, 'a', none, true, 1e16, -0.5] | tojson }} {{ s | tojson }} {{ {} | tojson }} {{ [] | tojson }}",
     {"s": "tab\there \"q\" \\ é   \x01 \x7f"}),
    ("tojson_indent", "{{ {'b': 1, 'a': [1, {'c': none}], 'e': {}} | tojson(indent=2) }}", {}),
    ("tojson_sorted_ascii", "{{ {'b': 1, 'a': 'é😀'} | tojson(sort_keys=true) }} {{ 'é😀' | tojson(ensure_ascii=true) }}", {}),
    ("string_and_safe", "{{ 2 | string }} {{ none | string }} {{ [1] | string }} {{ 1.5 | safe }} {{ x | safe }}|", {}),
    ("join_first_last_list", "{{ ['a', 'b'] | join('/') }} {{ [1, 2] | join }} {{ [3, 1] | first }} {{ 'xyz' | last }} {{ 'ab' | list }} {{ [] | first }}", {}),
    ("items_filter", "{{ d | items | list }} {{ x | items | list }}", {"d": {"a": 1, "b": [2]}}),
    # methods
    ("split", "{{ 'a,b,,c'.split(',') }} {{ '  a  b '.split() }} {{ 'a b c'.split(none, 1) }} {{ 'a,b,c'.split(',', 1) }} {{ ''.split() }} {{ ''.split(',') }}", {}),
    ("strip_family", "[{{ 'xxhixx'.strip('x') }}][{{ '  hi  '.lstrip() }}][{{ 'hi\\n\\n'.rstrip('\\n') }}][{{ '\\n hi \\t'.strip() }}]", {}),
    ("starts_ends_replace", "{{ 'abc'.startswith('a') }} {{ 'abc'.endswith(('x', 'c')) }} {{ 'a-b-c'.replace('-', '+') }} {{ 'a-b-c'.replace('-', '+', 1) }}", {}),
    ("join_method", "{{ ', '.join(['a', 'b']) }}|{{ ''.join('abc') }}", {}),
    ("dict_methods", "{{ d.items() | list }} {{ d.keys() | list }} {{ d.values() | list }}", {"d": {"a": 1, "b": 2}}),
    # tests
    ("tests", "{{ x is defined }} {{ x is undefined }} {{ none is none }} {{ 'a' is string }} {{ {} is mapping }} "
              "{{ 'a' is sequence }} {{ {} is sequence }} {{ 1 is sequence }} {{ x is sequence }} {{ 'a' is iterable }} {{ x is iterable }} {{ 1 is iterable }} "
              "{{ 1 is number }} {{ true is number }} {{ 1.5 is float }} {{ true is boolean }} {{ 1 is integer }} {{ true is integer }} "
              "{{ false is false }} {{ 0 is false }} {{ true is true }} {{ 'a' is not string }}", {}),
    # errors
    ("raise", "before{{ raise_exception('stop: ' ~ 3) }}after", {}),
    ("undefined_attribute", "{{ x.y }}", {}),
    ("str_plus_int", "{{ 'a' + 1 }}", {}),
    ("call_undefined", "{{ x() }}", {}),
    ("assign_attribute_of_non_namespace", "{% set d = {'a': 1} %}{% set d.a = 2 %}{{ d.a }}", {}),
    ("range", "{{ range(3) | list }} {{ range(1, 7, 2) | list }} {{ range(3, 0, -1) | list }}", {}),
]


def main():
    out_path = sys.argv[1]
    env = ImmutableSandboxedEnvironment(trim_blocks=True, lstrip_blocks=True, extensions=[loopcontrols])
    env.filters["tojson"] = tojson
    env.globals["raise_exception"] = raise_exception

    cases = []
    for name, source, variables in SNIPPETS:
        case = {"name": name, "template": source, "variables": variables}
        try:
            case["rendered"] = env.from_string(source).render(**variables)
        except TemplateError as e:
            case["error"] = str(e)
            case["error_type"] = type(e).__name__
        except Exception as e:  # TypeError and friends: the template met a value it cannot use
            case["error"] = str(e)
            case["error_type"] = type(e).__name__
        cases.append(case)

    out = {
        "_comment": "Small templates rendered by jinja2 the way Hugging Face renders chat templates. "
                    "JinjaLite must match `rendered` byte for byte, and fail where `error` is set "
                    "(raise_exception's message exactly when error_type is TemplateError).",
        "_source": {"renderer": "jinja2 " + jinja2.__version__, "made": datetime.date.today().isoformat()},
        "cases": cases,
    }
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"wrote {out_path}: {len(cases)} snippets, {sum(1 for c in cases if 'error' in c)} failing on purpose")


if __name__ == "__main__":
    main()
