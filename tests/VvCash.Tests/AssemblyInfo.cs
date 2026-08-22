using Xunit;

// Avalonia's Dispatcher.UIThread is a process-wide singleton, and four classes in this
// suite drive it with RunJobs() to prove their subjects marshal correctly:
// ExpenseDocumentServiceTest, PosViewModelSellerGateTest, ShiftServiceTest and
// UpdateViewModelTest. xunit's default runs collections in parallel with one collection
// per class, so those four raced each other inside DispatcherPriorityQueue and a varying
// test failed on roughly two runs in three — a baseline nobody could read a real
// regression against.
//
// Serialising the whole assembly rather than grouping the four into one [Collection]:
// it costs roughly two seconds — measured runs went from about a second in parallel to
// three or four serialised — which is nothing next to a baseline nobody can read, and a
// per-class opt-in is a rule the next dispatcher-touching test has to remember to join.
// This one cannot be forgotten.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
