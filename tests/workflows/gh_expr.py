"""An evaluator for the subset of GitHub Actions expressions that workflow `if:` conditions in this
repo use, so tests can decide a condition against event payloads the way GitHub would.

Supported: `||`, `&&`, `!`, `==`, `!=`, parentheses, property access (including the `.*.` filter),
string, number, boolean and null literals, `contains()` and `fromJSON()`. Comparisons and
`contains()` ignore case; `||` and `&&` return an operand. Anything else raises ValueError, so an
unsupported construct fails loudly instead of being misread. Tests: ExpressionEvaluator in
test_claude_workflow.py.
"""

import json
import re

TOKEN = re.compile(
    r"\s*(?:(?P<str>'(?:[^']|'')*')|(?P<num>\d+)|(?P<op>&&|\|\||==|!=|[!().,*\[\]])"
    r"|(?P<ident>[A-Za-z_][A-Za-z0-9_-]*))"
)


class Expr:
    """Recursive-descent evaluator with GitHub's semantics for what the condition uses:
    `||`, `&&`, `!`, `==`, `!=`, parentheses, property access (including the `.*.` filter),
    string literals, `contains()` and `fromJSON()`. Comparisons and `contains()` ignore case;
    `||` and `&&` return an operand, as in GitHub."""

    def __init__(self, text, context):
        self.tokens = []
        pos = 0
        text = text.strip()
        while pos < len(text):
            m = TOKEN.match(text, pos)
            if not m or m.end() == pos:
                raise ValueError(f"cannot tokenize at: {text[pos:pos + 30]!r}")
            kind = m.lastgroup
            self.tokens.append((kind, m.group(kind)))
            pos = m.end()
            while pos < len(text) and text[pos].isspace():
                pos += 1
        self.i = 0
        self.ctx = context

    def evaluate(self):
        value = self.or_()
        if self.i != len(self.tokens):
            raise ValueError(f"unexpected token {self.tokens[self.i]}")
        return value

    def peek(self):
        return self.tokens[self.i][1] if self.i < len(self.tokens) else None

    def take(self, expected=None):
        kind, value = self.tokens[self.i]
        if expected is not None and value != expected:
            raise ValueError(f"expected {expected!r}, got {value!r}")
        self.i += 1
        return kind, value

    def or_(self):
        left = self.and_()
        while self.peek() == "||":
            self.take()
            right = self.and_()
            left = left if truthy(left) else right
        return left

    def and_(self):
        left = self.cmp()
        while self.peek() == "&&":
            self.take()
            right = self.cmp()
            left = right if truthy(left) else left
        return left

    def cmp(self):
        left = self.unary()
        while self.peek() in ("==", "!="):
            op = self.take()[1]
            right = self.unary()
            equal = loose_equal(left, right)
            left = equal if op == "==" else not equal
        return left

    def unary(self):
        if self.peek() == "!":
            self.take()
            return not truthy(self.unary())
        return self.primary()

    def primary(self):
        kind, value = self.take()
        if value == "(":
            inner = self.or_()
            self.take(")")
            return inner
        if kind == "str":
            return value[1:-1].replace("''", "'")
        if kind == "num":
            return int(value)
        if kind != "ident":
            raise ValueError(f"unexpected {value!r}")
        if value in ("true", "false"):
            return value == "true"
        if value == "null":
            return None
        if self.peek() == "(":
            return self.call(value)
        return self.path(self.ctx.get(value))

    def call(self, name):
        self.take("(")
        args = [self.or_()]
        while self.peek() == ",":
            self.take()
            args.append(self.or_())
        self.take(")")
        if name == "contains":
            haystack, needle = args
            if isinstance(haystack, list):
                return any(loose_equal(item, needle) for item in haystack)
            return to_str(needle).lower() in to_str(haystack).lower()
        if name == "fromJSON":
            return json.loads(args[0])
        raise ValueError(f"unsupported function {name}")

    def path(self, value):
        while self.peek() == ".":
            self.take()
            kind, name = self.take()
            if name == "*":
                value = value if isinstance(value, list) else []
                self.take(".")
                _, field = self.take()
                value = [item.get(field) for item in value if isinstance(item, dict)]
            elif isinstance(value, list):
                value = [item.get(name) for item in value if isinstance(item, dict)]
            else:
                value = value.get(name) if isinstance(value, dict) else None
        return value


def truthy(value):
    return value not in (None, False, 0, "") and value == value


def to_str(value):
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def loose_equal(a, b):
    if isinstance(a, str) and isinstance(b, str):
        return a.lower() == b.lower()
    return a == b
