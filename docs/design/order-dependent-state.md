# State one test class leaves for the next (proposal, 2026-10-08)

Status: **measuring** (owner, 2026-10-08: measure first). The recorder writes `leakedReads` into the map (below);
selection does not read it.

## What happened

The clean NLog rerun of the final round (`runs-final-1007-nlog-clean`, after the output-folder fix of
`5b706e4`) has one miss: the `flip-branch` mutant of `NLog.Internal.Fakeables.AppEnvironmentWrapper.AppDomainConfigurationFile`
is missed in `NLog.UnitTests.LogManagerTests`.

- `LogManager.GetLogger` reads `LogFactory.Configuration`. Its getter returns `_config` when `_configLoaded` is
  true, and otherwise looks for a config file (`LoggingConfigurationFileLoader.Load`, which reads
  `AppDomainConfigurationFile`). `_configLoaded` becomes true only when a configuration is set or found; a search
  that finds nothing leaves it false.
- `LogManager` is global. A test class that sets `LogManager.Configuration` and does not reset it leaves
  `_configLoaded == true` for whatever class runs next in the same process.
- In the recording (classes one at a time, in the runner's order) `LogManagerTests` ran after such a class: its
  `GetLogger` calls returned the leftover configuration and never searched. The map records no
  `AppDomainConfigurationFile` for it. In an earlier recording of the same commit (another order) it did.
- With the mutant, the full run passes `LogManagerTests` (same order as the recording). The run of the classes
  TestDetta left out does not, because without the selected classes `LogManagerTests` comes first and searches; alone
  it fails with the mutant and passes without it. By the strict rules that is a miss.

So the class's own code path depends on state another class left behind. The map is right about the order it saw
and wrong about any other order: alone, first in a shard, or after a class that resets the configuration.

## Why the existing signals miss it

- `builtValues` links a class that reads a tracked field to the code that stored the value. `LogManagerTests` reads
  `_config`, but the value there was built by the earlier class's explicit configuration, not by the file search, so
  the link points at the wrong code. The path the class takes when the field is unset never ran in its bucket.
- `_configLoaded` is a `bool`, and mutable value-type fields are not tracked (a counter would store on every call).
- The single-bucket rule removed on 2026-10-06 would not have caught it either: the loader ran in several classes.

## The signal

The recorder can see the leak: a class reads a tracked field whose current value was stored while **another test
class** ran (the tracker knows the storing bucket through `Stored(field, methodId)`). That reader's path depends on
the order. What it would run with the field unset is unknown, but the code that decides it is known: the method that
read the field (`Configuration.get`) and whatever that method calls when the value is absent.

## Options

1. **Accept and document.** The selection matches the order the team's own runs use: the full run with the mutant
   passes `LogManagerTests` too. A team only sees this failure when the class runs alone or first. The judging rule
   stays strict, so such mutants stay misses in our numbers; we would explain them as order-dependent.
2. **Mark order-dependent readers in the map** (recorder: a class that reads a tracked field another class stored is
   listed with the field's reading method). Selection: a change to that reading method, or to anything it can reach
   by name, selects the reader. Narrow, but needs name reachability from the reading method, and a decision on how far.
3. **Record suspicious classes a second time in isolation** (one process per class that read another class's state)
   and union what they ran into their bucket. Precise, costs one extra process per such class (NLog: probably
   dozens), and only covers "alone", not every order.

## Recommendation

First measure how common it is: count, per map in the corpus, the classes that read a value another class stored
(a recorder-only change, no selection change), and how many of the corpus mutants such classes could hide. If it is
rare (NLog's global `LogManager` and a few singletons), option 3 for those classes is cheap and precise. If it is
common, option 2. Option 1 is the fallback, with the number stated next to the miss rate.

## Measurement (implemented 2026-10-08)

The tracker remembers, per tracked field, the test class whose test code stored the value it holds
(`TestDettaTracker.NoteStore`; a store in a static constructor, a cache fill, at start-up or in a fixture clears it).
A read by another test class is noted (`NoteLeakedRead`) and written as `leakedReads`; the recorder names the fields
(`CoverageRecorder.LeakedReads`) and the map keeps them per class (`CoverageMap.LeakedReads`, optional, format 2).
Fields the recorder does not track (value types such as NLog's `_configLoaded`) are not seen; NLog's `_config` is.
Next: re-record the corpus and count, per map, the classes with leaked reads.
