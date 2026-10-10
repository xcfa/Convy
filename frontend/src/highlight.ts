// Syntax highlighting for rules.yaml: a line-based YAML tokenizer, plus the condition
// language (TorrentFilter.g4) inside `condition:` values. Produces tokens, never HTML.

export type TokenKind =
  | "plain"
  | "comment"
  | "key"
  | "punct"
  | "dash"
  | "string"
  | "number"
  | "bool"
  | "op"
  | "paren"
  | "prop"
  | "value"
  | "func";

export interface Token {
  text: string;
  kind: TokenKind;
}

const comparators = ["==", "!=", ">=", "<=", ">", "<"];
const operators = ["&&", "||", ...comparators, "!"];

/**
 * Tokens of a condition such as `Category == Music && !Tags.Contains(skip)`. With
 * `escapedQuotes` (the text of a double-quoted YAML string as written in the file), strings
 * inside the condition are delimited by `\"` rather than `"`.
 */
export function highlightCondition(text: string, escapedQuotes = false): Token[] {
  const tokens: Token[] = [];
  const quote = escapedQuotes ? '\\"' : '"';
  let i = 0;

  const push = (kind: TokenKind, value: string) => {
    if (value) tokens.push({ text: value, kind });
  };

  while (i < text.length) {
    const rest = text.slice(i);

    const space = /^\s+/.exec(rest);
    if (space) {
      push("plain", space[0]);
      i += space[0].length;
      continue;
    }

    if (rest.startsWith(quote)) {
      let end = i + quote.length;
      while (end < text.length && !text.startsWith(quote, end)) {
        end += !escapedQuotes && text[end] === "\\" ? 2 : 1;
      }
      end = Math.min(text.length, end + quote.length);
      push("string", text.slice(i, end));
      i = end;
      continue;
    }

    const op = operators.find((o) => rest.startsWith(o));
    if (op) {
      push("op", op);
      i += op.length;
      continue;
    }

    if (rest[0] === "(" || rest[0] === ")") {
      push("paren", rest[0]);
      i += 1;
      continue;
    }

    if (rest[0] === ".") {
      push("punct", ".");
      i += 1;
      continue;
    }

    const number = /^-?\d+(\.\d+)?/.exec(rest);
    if (number) {
      push("number", number[0]);
      i += number[0].length;
      continue;
    }

    const word = /^[A-Za-z_]\w*/.exec(rest);
    if (word) {
      const value = word[0];
      const after = text.slice(i + value.length).trimStart();
      const kind: TokenKind =
        value === "true" || value === "false"
          ? "bool"
          : value === "Contains" && tokens.at(-1)?.text === "."
            ? "func"
            : after.startsWith(".") || comparators.some((c) => after.startsWith(c))
              ? "prop"
              : "value";
      push(kind, value);
      i += value.length;
      continue;
    }

    push("plain", rest[0]);
    i += 1;
  }

  return tokens;
}

/** Tokens per line of a YAML document (rules.yaml). */
export function highlightYaml(text: string): Token[][] {
  return text.replace(/\r\n?/g, "\n").split("\n").map(highlightYamlLine);
}

function highlightYamlLine(line: string): Token[] {
  const tokens: Token[] = [];
  const push = (kind: TokenKind, value: string) => {
    if (value) tokens.push({ text: value, kind });
  };

  let rest = line;
  const indent = /^\s*/.exec(rest)![0];
  push("plain", indent);
  rest = rest.slice(indent.length);

  if (rest.startsWith("#")) {
    push("comment", rest);
    return tokens;
  }

  const dash = /^-(\s+|$)/.exec(rest);
  if (dash) {
    push("dash", "-");
    push("plain", dash[1]);
    rest = rest.slice(dash[0].length);
  }

  let key: string | null = null;
  const keyMatch = /^([A-Za-z_][\w-]*)(\s*:)(\s|$)/.exec(rest);
  if (keyMatch) {
    key = keyMatch[1];
    push("key", keyMatch[1]);
    push("punct", keyMatch[2]);
    rest = rest.slice(keyMatch[1].length + keyMatch[2].length);
  }

  const space = /^\s*/.exec(rest)![0];
  push("plain", space);
  rest = rest.slice(space.length);

  if (rest.startsWith('"') || rest.startsWith("'")) {
    const q = rest[0];
    let end = 1;
    while (end < rest.length && rest[end] !== q) {
      end += q === '"' && rest[end] === "\\" ? 2 : 1;
    }
    const closed = Math.min(rest.length, end + 1);
    const inner = rest.slice(1, Math.min(end, rest.length));

    push("string", q);
    if (key === "condition") tokens.push(...highlightCondition(inner, q === '"'));
    else push("string", inner);
    if (end < rest.length) push("string", q);

    rest = rest.slice(closed);
    pushTrailing(rest, push);
    return tokens;
  }

  // A plain scalar runs up to a comment (" #").
  const comment = rest.search(/\s#/);
  const scalar = comment >= 0 ? rest.slice(0, comment) : rest;
  if (key === "condition") tokens.push(...highlightCondition(scalar));
  else push(scalarKind(scalar.trim()), scalar);
  pushTrailing(comment >= 0 ? rest.slice(comment) : "", push);
  return tokens;
}

function pushTrailing(rest: string, push: (kind: TokenKind, value: string) => void) {
  const comment = rest.indexOf("#");
  if (comment < 0) {
    push("plain", rest);
    return;
  }
  push("plain", rest.slice(0, comment));
  push("comment", rest.slice(comment));
}

function scalarKind(value: string): TokenKind {
  if (/^-?\d+(\.\d+)?$/.test(value)) return "number";
  if (/^(true|false|null|~)$/i.test(value)) return "bool";
  return value ? "string" : "plain";
}
