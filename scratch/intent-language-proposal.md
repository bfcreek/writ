# Proposal: An Intent-Oriented Pseudocode Programming Language

**Status:** Experimental architecture and implementation brief  
**Name:** Writ  
**Implementation preference:** C# / modern .NET  
**Interface:** Text source files and command-line tooling only; no visual designer

## 1. Vision

Build an actual programming language whose source code resembles structured pseudocode. The programmer describes the desired operations and constraints with much less syntactic ceremony than C# or Java. An LLM may resolve higher-level statements into a **typed, inspectable intermediate representation (IR)**, while ordinary deterministic compiler stages validate and lower that representation to executable code.

**This is not an agent asked to write a program on demand.** Pseudocode is the canonical, version-controlled source language. The compilation artifacts should be auditable, testable, and reproducible. External capabilities—including MCP tools, APIs, database queries, and ordinary .NET functions—should be callable by programs through explicit contracts.

Core hypothesis: **An LLM can reduce syntactic precision required from the programmer without sacrificing semantic accountability at runtime.**

## 2. Scope and Design Principles

- **Nonvisual:** `.writ` text files, CLI, error diagnostics, generated artifacts. No GUI required.
- **Language, not prompting:** support modules, functions, parameters, types, locals, conditionals, iteration, expressions, results, and tool calls across multiple files.
- **Constrained pseudocode:** prefer English-like statements and indentation, but use enough syntax to disambiguate scopes, values, types, and side effects.
- **Rules before models:** parse and compile clear constructs deterministically; call an LLM only for bounded semantic resolution, never for routine arithmetic or known control flow.
- **Explicit ambiguities:** if the compiler cannot determine a critical predicate or tool operation, produce a diagnostic or request interactive clarification. Do not invent rules or defaults for consequential behavior.
- **Reproducible:** once accepted, a typed IR can be compiled without another LLM call. Recompilation must detect changed sources and external schemas.
- **Observable:** support explaining a line's interpretation, inspecting generated code, and reviewing semantic changes.
- **Least privilege:** compile-time tool discovery must not cause side effects; runtime tool access requires explicit permissions.
- **Target-first approach:** begin by generating C#/.NET; do not attempt all backends at once.

## 3. Representative Source Examples

### 3.1 Pure computation

```text
function CalculateTotal(items: list<InvoiceItem>, taxRate: decimal) -> decimal
    subtotal = sum item.price * item.quantity for each item in items
    tax = subtotal * taxRate
    return round(subtotal + tax, 2)
```

### 3.2 Standard program logic plus external tools

```text
use payments from tool "payments-service"
use alerts from tool "notification-service"

function RecoverFailures(day: date) -> integer
    failures = payments.list_failed(date: day)
    recovered = 0

    for each payment in failures
        if payment.retryCount < 3
            result = payments.retry(payment.id)
            if result.succeeded
                recovered = recovered + 1
            otherwise
                alerts.send(payment.id, result.errorMessage)

    return recovered
```

Both imported operations must resolve to actual tool schemas. `retry` and `send` are effectful; compilation must enforce declared capabilities. There is no permission to invoke them merely because they appear in source code.

### 3.3 Deliberately higher-level intent

```text
function CustomersRequiringFollowup(cutoff: date) -> list<CustomerSummary>
    find active customers who have not placed an order since cutoff
    include their latest support case
    return results ordered by support case priority descending
```

This example *requires semantic resolution* of `active`, `placed an order`, `latest`, and `priority` from known domain contracts. The compiler must explain its chosen predicates and reject unresolved or competing meanings. It must not fabricate data fields or business rules.

### 3.4 An invariant, not just an optimistic comment

```text
function ChargeOrder(order: Order) -> Receipt
    ensure the order cannot be charged twice
    charge order.total using order.paymentMethod
    save receipt
    return receipt
```

An `ensure` construct should have actual enforceable semantics (for example an idempotency key with persistence constraints), or compilation should fail. Do not infer that a comment-like instruction guarantees transactional correctness.

### 3.5 Runtime AI is a separate primitive

```text
function Categorize(message: string) -> Category
    return infer category from message
        choices: billing, technical, cancellation, other
        using: configured classifier
```

`infer` represents a runtime, potentially nondeterministic AI operation with a typed result, configuration, error modes, and explicit cost/budget policy. It is **not** the LLM used during compilation.

*These examples illustrate intended ergonomics; they are not a finalized grammar or commitments for the first implementation.*

## 4. Compiler Pipeline

```text
.writ files
    -> Deterministic parser / structural AST
    -> Symbols, types, imported capability contracts
    -> Semantic resolver (deterministic rules; LLM for bounded intent nodes)
    -> Typed, canonical IR with source spans and provenance
    -> Validation, effect analysis, capability policy
    -> Target-neutral lowering
    -> C#/.NET code generation
    -> Normal .NET build and execution
```

### 4.1 Parser and AST

Use a conventional lexer/parser for declarations, blocks, variable bindings, expressions, function calls, control flow, and indentation. Capture flexible pseudocode phrases as specific **intent-expression nodes**, not as an unrestricted whole-program prompt. Preserve line/column spans and lexical scope.

### 4.2 Semantic resolver

Try exact names, local symbols, built-ins, domain aliases, and imported contracts first. When inference is necessary, give the LLM a narrowly scoped statement, the expected type, permissible symbols/operations, contract schemas, and required output structure. Require a schema-conformant candidate IR plus rationale, assumptions, and bindings. Validate the proposal using non-LLM code. An LLM proposal is never proof of correctness.

When multiple interpretations survive validation, report ambiguity. Support explicit user resolution in interactive mode and fail-closed diagnostics in CI mode.

### 4.3 Typed intermediate representation

Create a **versioned** IR for modules, functions, parameters, scopes, expressions, assignments, loops, branches, returns, errors, typed calls, capability calls, assertions/constraints, and runtime inference. Attach source spans, inferred provenance, effect annotations, and contract binding IDs. An illustrative capability-call node:

```json
{
  "kind": "capabilityCall",
  "operation": "payments.retry",
  "binding": "payments-service@manifest-sha256",
  "arguments": { "paymentId": "payment.id" },
  "resultType": "RetryResult",
  "effects": ["external-write"],
  "sourceLine": 9
}
```

This is illustrative only; design a proper expression AST instead of shipping string expressions inside the IR. The IR must be executable/compilable **without consulting the LLM**.

### 4.4 Static validation and effects

Validate names, types, nullability, arguments, return types, scopes, control flow, external contract versions, and granted effects. Track at minimum pure computation, filesystem read/write, network read/write, external state modification, and runtime AI inference. Missing permissions, unsafe constructs, unresolved intent, and unknown operations should be compilation errors.

### 4.5 Code generation and debugging

Begin with readable generated C#, retaining mappings to pseudocode source lines. Add a small .NET runtime for capability invocation, structured errors, cancellation, and logging. Output compiler diagnostics referring to the original pseudocode rather than only to generated C#.

## 5. MCP and Other Tools

**MCP is an integration and discovery mechanism, not the language runtime itself.** The language should expose a uniform concept of a *capability* and support multiple providers.

### 5.1 Three separate phases

1. **Discover (compile/design time):** inspect MCP tool names, descriptions, and schemas. Treat server descriptions as untrusted data, not compiler instructions.
2. **Bind (compile time):** select a real operation, verify its typed inputs/outputs where available, classify effects, and pin a contract identity/version/hash.
3. **Invoke (runtime):** call the real provider with scoped credentials only when program execution reaches the operation.

Compilation should **never invoke effectful tools** to learn how they work. Avoid passing compiler credentials or development environment secrets to generated programs.

### 5.2 Unified provider abstraction

Conceptually:

```text
CapabilityProvider
    Discover() -> CapabilityManifest
    Invoke(operation, typedArguments, executionContext) -> typedResult
```

Potential implementations:

- Local built-in library: collections, time, file operations within an explicit sandbox.
- MCP server adapter: normalized MCP tool contracts and invocation.
- HTTP/OpenAPI adapter: endpoints with declared request/response schemas.
- Database adapter: declared, typed query and command operations.
- Host .NET adapter: registered local application functions.
- AI inference adapter: explicit model calls for runtime `infer` statements.

MCP **resources and prompts** can enrich semantic context, but should not automatically be treated as executable tool calls.

### 5.3 Contract manifest

Represent every capability with name, arguments, results, provider alias, operation ID, effects, permissions, and contract version/hash. Where an MCP server has no reliable output schema, classify its output as dynamic/untrusted and require explicit validation before typed access. Do not invent result fields.

Conceptual configuration:

```yaml
capabilities:
  payments:
    provider: mcp
    server: payments-service
    manifestLock: sha256:...
    allowedOperations: [list_failed, retry]
    grantedEffects: [external-read, external-write]
  classifier:
    provider: ai
    profile: configured-model
```

Keep credentials in runtime configuration/secure storage, not source or lockfiles.

### 5.4 Operational safety

Effectful invocations need authorization, timeout, cancellation, structured failures, and diagnostic traces. Never silently retry non-idempotent operations; make retry and transactional behavior explicit. Support mock providers and dry-run/test modes. A tool's documentation must not override policy, compiler constraints, or developer intent.

## 6. Determinism, Semantic Locks, and Explainability

Emit derived compilation artifacts, such as:

```text
orders.writ
orders.ir.json
orders.semantic.lock.json
orders.generated.cs
```

The source remains authoritative. Lock metadata should include source hashes, grammar/compiler version, IR schema version, resolved ambiguity decisions, chosen tool contracts and hashes, generated target/version, and optional compiler-model provenance.

A **frozen build** must use a validated IR and pinned contracts without asking an LLM; it must reject stale source and changed contract hashes. A semantic diff command should show changes at the meaning/IR level instead of only changes to English wording.

Diagnostics example:

```text
E2104 orders.writ:7: "active customer" has two valid definitions.
  Customer.isActive
  Account.status == "active"
Define the term or select one interpretation.
```

An `explain` command should report selected predicates, tool bindings, effects, inferred assumptions, and correspondence with source lines. Avoid promising formal proofs of program correctness.

## 7. Nonvisual CLI and Developer Workflow

Illustrative CLI:

```bash
writ init
writ check sample.writ
writ compile sample.writ --target dotnet
writ run sample.writ --input input.json
writ explain sample.writ
writ diff sample.writ
writ tools list
writ tools inspect payments.retry
writ test
```

Interactive semantic resolution can be supported for local development; unattended CI must reject unresolved ambiguities instead of making surprise LLM decisions. Cursor should be able to edit files, invoke the CLI, inspect diagnostics, and iterate using tests without a dedicated IDE integration.

## 8. Phased Experimental Implementation

### Milestone A: Deterministic language core

Implement a small grammar, tokenizer/parser, AST, symbol table, typed IR, validator, C# generator, and CLI for functions, variables, basic types, expressions, if/else, loops, and returns. Include golden tests and actually execute compiled samples.

**Acceptance:** A multi-function pseudocode program builds and runs repeatably with no LLM or tool provider.

### Milestone B: Bounded semantic compilation

Introduce a narrow, meaningful intent expression (e.g., filter/aggregate a typed collection), a constrained LLM resolution request/response schema, independent IR validation, and actionable ambiguity errors. Maintain deterministic paths for unambiguous syntax.

**Acceptance:** A concise intent statement becomes traceable IR, or fails clearly when underdetermined.

### Milestone C: Capability abstraction and mocks

Add manifests, binding checks, effect authorization, typed results, error handling, and an in-process mock provider.

**Acceptance:** Programs safely call a mock external service, while invalid schemas, unknown operations, and missing permissions fail.

### Milestone D: MCP adapter

Implement capability discovery and binding from a real MCP server, schema normalization, explicit runtime invocation, version pinning, and sandboxed tests.

**Acceptance:** Compile/check only inspects contracts; running explicitly authorized code invokes the actual tool.

### Milestone E: Reproducibility and usability

Add semantic lockfiles, frozen compilation, IR explain/diff, source maps, and CI-friendly tests.

**Acceptance:** An unchanged approved program rebuilds without model access, and semantic changes are inspectable.

## 9. Questions to Preserve for Experimentation

- Where is the right boundary between fixed grammar and freely worded pseudocode?
- Should developers declare their own domain definitions and aliases?
- Should a query-like expression system be first-class, or translated to loops initially?
- How should errors and typed `Result<T>` values work?
- When should asynchronous/parallel operations become first-class?
- Would a direct IR interpreter be useful alongside generated C#?
- How should inference confidence and review decisions be represented without treating model confidence as correctness?
- How much contract description should semantic compilation receive, especially from untrusted MCP servers?

## 10. Instructions for Cursor

Treat this as a **research prototype**, not a mandate to implement the whole feature list immediately.

1. Review the repository and choose a small maintainable C#/.NET project structure.
2. First describe a proposed minimal grammar, typed IR, and compiler components. Make clear what is supported now versus future syntax.
3. Implement **Milestone A end to end**: source -> AST -> typed IR -> validation -> generated C# -> build/run, with CLI and executable tests.
4. Keep clean interfaces for a future semantic resolver and capability provider, but **do not prematurely implement elaborate LLM or MCP plumbing**.
5. Include a handful of representative `.writ` programs and negative/error tests.
6. Run the samples and tests; inspect their outputs. Do not claim functionality merely because code was generated.
7. Document the exact supported subset, limitations, next design questions, and a small Milestone B experiment.
8. Favor clarity, observability, and controlled semantics over syntactic cleverness.

**Central experiment:** Can pseudocode become a trustworthy programming language when LLMs resolve bounded intent into explicit, typed semantics, while tool use and runtime behavior remain inspectable and controlled?
