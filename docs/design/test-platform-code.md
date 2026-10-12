# Test platform code before the tests

Status: owner's decision 2026-10-12 (Newtonsoft option a, channel 1 only), branch feat/platform-startup-code, being
measured. It narrows.

## Problem

Newtonsoft.Json's tests run under VSTest, whose testhost serializes its messages with Newtonsoft.Json, and the one it
loads is the repository's own, instrumented build in the test output. Code the platform runs before the first test
lands in the start-up bucket and so counts as code outside every test: a change to it selects every class through the
rules for code outside the tests, and the newtonsoft-json share stayed at 35.7 %, against an upper bound of about
20.7 % with platform code apart.

A diagnostic recording (diag/platform-channels, 2026-10-12) split the platform's hits by where they land:

| Channel | Hits | Platform alone | Note |
|---|---|---|---|
| Before the first test (start-up bucket) | 111,248 | 110,660 (99.5 %), 661 methods | the code outside every test |
| On the host's own threads during the tests | 12.4 million | 17,654 | the rest has the test framework on the stack too |
| Between tests on the test's flow | 0 | 0 | |

Only the first channel is taken up here.

## Design

- `TestDettaTracker.Hit`: a hit of the start-up bucket for a method not yet outside every test walks the stack once
  (`RunsForTestPlatformOnly`). Past the method's own assembly and the base class library, every caller must be the test
  platform (`Microsoft.TestPlatform.*`, `Microsoft.VisualStudio.TestPlatform.*`, `testhost`), and there must be at least
  one. Such a hit goes to `PlatformBits`; any other (another repository assembly, a test framework, a test, no caller at
  all) to the start-up bucket as before. Once a method is outside every test it is not asked again, so the cost is a
  stack walk per start-up hit of a method the platform alone has called so far.
- The recorder reads the `platform` ids. A method that some test class or fixture also ran counts only for them. One
  that no test ran goes back into the ambient list, since every run of the project runs it and a change to it must
  still select the project. The list is kept in the map (`CoverageMap.TestPlatformCode`), for information.

## Safety

Only methods that the tests ran too lose their "outside every test" credit; the classes that ran them are selected for
a change to them. A behaviour change that breaks only the platform's use (a message the testhost writes, not a value a
test reads) is caught by any run of the project, and one of those classes runs. A start-up hit with anything but the
platform below it stays outside every test.

## Tests

`TestPlatformCodeTests`: a method called on its own thread by a dynamic assembly named `Microsoft.TestPlatform.Probe`
runs for the platform alone; the same method called from a test, or from a bare thread, does not.
