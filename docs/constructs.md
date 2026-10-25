# PowerShell constructs in Writ

Checklist against the Windows PowerShell 3.0 language specification in `docs/ps`, plus the PowerShell 7 operators Writ targets. Cmdlets stay in the command catalog. Workflows are not on this list: PowerShell 7 does not run them.

## Paragraphs and pronouns

A loop or a test can be a sentence, or two sentences, instead of an indented block. A comma, `and`, or a period continues the same thought. A blank line, or a new sentence that names something else, ends it.

`it` means the most recent single value. Inside `for each number`, that is the number. After the sentence, it is whatever was named before the loop. `they`, `them`, and `their` mean the most recent list. `its length` and `their count` are the length of that value.

```text
the total is 0
for each number from 1 to 3, add it to the total
return it

the files are a list containing "a" and "bb"
for each file in the files, add its length to the total. If it is 2, skip it.

keep increasing it until it is 3
```

`add X to the file` still writes a file. `add X to the total` adds to that value. `skip it` goes to the next item. `skip the rest` and `stop there` leave the loop.

## Statements

- [x] `break` / `break the loop` / `break to Label`
- [x] `continue` / `continue the loop` / `continue to Label`
- [x] `exit the process` / `exit the process with N` (`exit with "message"` is still `throw`)
- [x] `repeat` … `while` / `until`
- [x] `for the i from 1 to 10` (`for each` is unchanged)
- [x] `switch on` / `when` / `otherwise`
- [x] `trap errors`
- [x] `data` (the block runs; its output is discarded, because Writ returns only through `return`)
- [x] `mark Label` on the next statement
- [x] `increase` / `decrease` / `multiply` / `divide` / `remainder` … `by` (`increase` and `decrease` without `by` are `++` and `--`)
- [x] `redirect` output, errors, or all output, including append and `to the output`
- [x] `import the module` / `remove the module` / `source the script` (needs `Allow running scripts`)
- [x] `before` / `on each` / `after` / `when binding parameters` (`begin` / `process` / `end` / `dynamicparam`)
- [x] `using the namespace` / `using the module`
- [x] `export the function`
- [ ] `workflow`, `parallel`, `sequence`, `inlinescript` — removed in PowerShell 6+
- [ ] Implicit statement output — Writ returns only through `return`

## Expressions

- [x] `nothing` / `null`
- [x] `1 through 10`
- [x] `a table of name set to "Ada"` and `an ordered table of` …
- [x] `a block of` … and `a block of` … `closed`
- [x] `the output of` / `the collected output of`
- [x] `the character "A"`
- [x] `the item at` / `the last item of` / `the items from` … `to`
- [x] `a grid of 2 by 3 integers`
- [x] `whether it succeeded`, `the pipeline item`, `the arguments`, `the errors`
- [x] `the global` / `local` / `private` / `script` name (`the script folder` stays the script folder)
- [x] `the static Member of Type` and `calling Member`
- [x] `when present` (`?.`)
- [x] `spread` / `splatted`
- [x] `call the command "Get-Date"`
- [x] `is like` / `is not like` / `is in` / `is not in` / `is a Type` / `is not a Type` / `is nothing`
- [x] `contains` / `does not contain`
- [x] `matches` / `does not match`
- [x] `joined` / `joined by` / `split on` / `replacing` … `with`
- [x] `formatted with` (`-f`). Quoted `{…}` is still Writ interpolation
- [x] `piped to`
- [x] `bitwise and` / `or` / `xor`, `bitwise not`, `shifted left` / `right`
- [x] `xor`
- [x] `A otherwise B` (`??`)
- [x] `A when condition otherwise B`
- [x] `or else` (`||`) and `and then` (`&&`). These follow PowerShell: the next part runs when the previous command succeeded or failed, not when its value is true or false. The generated script stops on errors, so a failing command throws before the chain.
- [x] `as a T if possible` (`-as`)
- [x] `2 kb` / `2kb` and the other size suffixes
- [ ] `??=` — use `otherwise` and assign
- [ ] Method overload resolution — the call is emitted and PowerShell resolves it

## Types, parameters, and declarations

- [x] character, float, table, pattern, xml, block, object, switch
- [x] `dictionary of K to V`, `grid of T`, `reference to T`
- [x] `defaulting to`, `from the pipeline`, `also called`, `one of`, `matching`
- [x] `with length from` / `with count from` / `from N to M`
- [x] `allowing` / `rejecting` nothing, empty text, or an empty list; `allowing wildcards`
- [x] `passing the test`
- [x] `To filter` and `To command`
- [x] `An enumeration Color of red, green and blue`
- [x] Methods: an indented `To` inside `An Widget has`
- [x] `fixed at` / `locked at`
- [ ] Type extension and ETS
- [ ] Anonymous description types (spec 4.5)
- [ ] Module manifests and install
- [ ] Provider drives beyond the environment, scopes, and the command catalog
