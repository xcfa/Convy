grammar TorrentFilter;

/*
 * A tiny boolean DSL used by ConvyMappings.ini to decide whether a torrent
 * should be routed to a given output path. Examples:
 *
 *   Size > 100 && Uploaded == true && Tags.Contains(Test) && Category == Test
 *   (State == downloading || State == stalledUP) && !Tags.Contains(skip)
 *   Name == "My Show" && Ratio >= 1.5
 *
 * The grammar is intentionally generic: the left-hand side of a comparison is
 * any identifier (a property name). Whether that property actually exists and
 * which operators/value types are legal for it is resolved later, while the
 * parse tree is turned into an IExpression tree.
 */

/* ------------------------------------------------------------------ parser */

filter
    : expression EOF
    ;

expression
    : LPAREN expression RPAREN                       # ParenExpression
    | NOT expression                                 # NotExpression
    | expression AND expression                      # AndExpression
    | expression OR expression                       # OrExpression
    | predicate                                      # PredicateExpression
    ;

predicate
    : property comparator value                      # ComparisonPredicate
    | property DOT CONTAINS LPAREN value RPAREN      # ContainsPredicate
    ;

property
    : IDENTIFIER
    ;

comparator
    : EQ | NEQ | GT | GTE | LT | LTE
    ;

value
    : BOOL
    | NUMBER
    | STRING
    | IDENTIFIER
    ;

/* ------------------------------------------------------------------- lexer */

AND      : '&&' ;
OR       : '||' ;
NEQ      : '!=' ;
NOT      : '!' ;
EQ       : '==' ;
GTE      : '>=' ;
LTE      : '<=' ;
GT       : '>' ;
LT       : '<' ;
LPAREN   : '(' ;
RPAREN   : ')' ;
DOT      : '.' ;

// Keywords must precede IDENTIFIER so they are not swallowed by it.
CONTAINS : 'Contains' ;
BOOL     : 'true' | 'false' ;

NUMBER   : '-'? [0-9]+ ('.' [0-9]+)? ;

// Double-quoted string with backslash escapes, e.g. "My \"quoted\" show".
STRING   : '"' ( '\\' . | ~["\\] )* '"' ;

IDENTIFIER : [a-zA-Z_] [a-zA-Z_0-9]* ;

WS       : [ \t\r\n]+ -> skip ;
