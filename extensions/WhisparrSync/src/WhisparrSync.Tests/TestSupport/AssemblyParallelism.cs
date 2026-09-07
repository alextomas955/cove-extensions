// Three classes in this assembly measure RETAINED BYTES through GC.GetTotalMemory, which is a process-wide
// reading: a neighbouring class holding tens to hundreds of megabytes live across one of their sampling
// instants is counted as that class's own retention. Without this exclusion a full suite run fails one memory
// class or another in roughly two attempts of three, and a sample can come back NEGATIVE — a collector
// releasing more than the read under measurement ever held.
//
// Naming a shared collection is not sufficient: xUnit joins classes by collection NAME only when each one
// declares the attribute, and a class that declares none is given a fresh collection of its own that no name
// can be matched to. So the exclusion has to be assembly-wide, and under xUnit v3 that is
// `parallelizeTestCollections: false` in this project's xunit.runner.json — the v3 replacement for v2's
// [assembly: CollectionBehavior(DisableTestParallelization = true)], which is obsolete and whose
// replacement attribute ships only in a runner package a test assembly does not reference.
