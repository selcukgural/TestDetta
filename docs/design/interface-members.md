# Interface members answered through their implementations

Status: implemented 2026-10-09 (owner approved the direction while dogfooding on AfterApply).

## Problem

AfterApply PR #202 added two optional parameters to the record `ApplicationDetailResponse`. Changed
metadata spreads the record's name, which reached the members of `IApplicationService` returning it.
An interface member has no body, so no map instruments it ("a map does not know it"), and the spread
went on by its simple name: `CreateAsync`, `ChangeStatusAsync`, `UpdateAsync`. The index is syntactic
and keeps no receiver types, so every call of any `CreateAsync` in the repository matched: the blog and
salary endpoints, `AuthService` calling `userManager.CreateAsync`, and through them every test class
that boots the app.

## Design

- **Index**: `IndexedMember.IsAbstract` marks a member with no code of its own: `abstract`, or an
  instance member of an interface without a body, an expression body or an accessor with one (C# only;
  Visual Basic and F# keep the name trace). `SourceIndex.Implementations` finds the members of the same
  simple name, with code, in every type whose base list names the member's type, directly or through
  other such types (an abstract class in between).
- **Selection** (`TestSelector.Trace`): when coverage cannot answer for an abstract member the spread
  reached through code, and the repository declares at least one implementation, and coverage answers
  for every one of them, the classes that ran them are the answer and the spread stops there.
  Otherwise the member's name is traced as before.
- **Why it is safe**: a call through the member runs one of its implementations. One in the repository
  is answered by its own coverage; one outside it (a mock, a proxy) runs none of the repository's
  changed code. A change to the member itself (its signature) is a change, not a reached member, and
  keeps today's rules; so does a member reached through metadata.
- **Matching by simple name** may add an unrelated overload of an implementing type, which only asks
  coverage about more code.

## Results

AfterApply PR #202, maps at the merge base: the interface members are answered through 1 or 2
implementations each, and no `CreateAsync`-style name is traced any more. The integration project still
selects 96 of 99 classes, through the endpoint registrations that name the record and run in every
class's host (`MapApplicationEndpoints`): a separate item, not part of this change.

Tests: `InterfaceMemberTests` (an interface with an abstract class in between, an unrelated member of
the same name; an interface without an implementation keeps the name trace).
