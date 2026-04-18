### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
SIA001 | IdAttribute.Usage | Warning | Id type mismatch between source and target
SIA002 | IdAttribute.Usage | Warning | Source has no Id while target requires one
SIA003 | IdAttribute.Usage | Warning | Source has Id while target has none
SIA004 | IdAttribute.Usage | Error | Multiple declarations map to the same conventional Id name
SIA005 | IdAttribute.Usage | Warning | [Id(...)] attribute matches the naming convention and is redundant
SIA006 | IdAttribute.Usage | Warning | [UnionId("x")] with a single option should be [Id("x")]
