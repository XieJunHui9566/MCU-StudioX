using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

if (args is not [var runtimeDirectory, var outputDirectory])
{
    Console.Error.WriteLine("Usage (repository root): dotnet run --project tools/StudioX.RtosValidation -- <existing runtime> <output>");
    return 2;
}
var runtime = Path.GetFullPath(runtimeDirectory);
var output = Path.GetFullPath(outputDirectory);
Directory.CreateDirectory(output);
var fixture = Path.GetFullPath("tools/StudioX.RtosValidation/Fixtures/freertos-snapshot.c");
var passed = new List<string>();
void Pass(string message) { passed.Add(message); Console.WriteLine("PASS " + message); }
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
var armRoot = Path.Combine(runtime, "toolsets", "arm.gnu", "1.0.0", "gcc", "bin");
var wchRoot = Path.Combine(runtime, "toolsets", "wch.riscv", "1.0.0", "gcc", "bin");
var armGdb = Path.Combine(armRoot, "arm-none-eabi-gdb.exe");
var armElf = await FixtureBuild.BuildAsync(Path.Combine(armRoot, "arm-none-eabi-gcc.exe"), fixture, Path.Combine(output, "arm"), true, false);
var minimalElf = await FixtureBuild.BuildAsync(Path.Combine(armRoot, "arm-none-eabi-gcc.exe"), fixture, Path.Combine(output, "arm"), true, true);
var wchElf = await FixtureBuild.BuildAsync(Path.Combine(wchRoot, "riscv-wch-elf-gcc.exe"), fixture, Path.Combine(output, "wch"), false, false);
Pass("Existing ARM and WCH compilers create DWARF ELF data fixtures without SDK downloads or hardware");

async Task InitializeAsync(GdbDebugAdapter adapter, string elf)
{
    await adapter.SendAsync("-gdb-set print elements 128");
    await adapter.SendAsync("-gdb-set may-call-functions off");
    await adapter.SendAsync("-gdb-set write off");
    await adapter.SendAsync("-file-exec-and-symbols " + MiRecord.Quote(elf.Replace('\\', '/')));
}

string Expression(string command)
{
    const string prefix = "-data-evaluate-expression ";
    return command.StartsWith(prefix, StringComparison.Ordinal)
        ? MiRecord.Parse("^done,value=" + command[prefix.Length..]).Data.String("value") : "";
}
void CheckReadOnly(IEnumerable<string> commands)
{
    foreach (var command in commands)
    {
        Check(command.StartsWith("-data-evaluate-expression ", StringComparison.Ordinal) ||
              command.StartsWith("-data-read-memory-bytes ", StringComparison.Ordinal) ||
              command == "-gdb-show architecture", "RTOS inspector emitted a non-read command: " + command);
        var expression = Expression(command);
        Check(!Regex.IsMatch(expression, @"(?<![=!<>])=(?!=)|\+\+|--|;|\n|\r"), "RTOS read contained assignment or command injection.");
        var calls = Regex.Matches(expression, @"\b([A-Za-z_]\w*)\s*\(");
        Check(calls.All(match => match.Groups[1].Value == "sizeof"), "RTOS read contained an inferior function call.");
    }
}

void CheckFixture(FreeRtosSnapshot snapshot)
{
    Check(snapshot.IsAvailable && snapshot.SchedulerRunning == true && snapshot.SchedulerSuspended == 0 &&
          snapshot.TickCount == 12345 && snapshot.ReportedTaskCount == 5, "Scheduler metrics differ from the initialized ELF fixture.");
    Check(snapshot.Tasks.Count == 5, "All ready, delayed, suspended and termination list tasks must be enumerated.");
    var sensor = snapshot.Tasks.Single(task => task.Name == "Sensor");
    var receiver = snapshot.Tasks.Single(task => task.Name == "Receiver");
    var suspended = snapshot.Tasks.Single(task => task.Name == "Maintenance");
    var retired = snapshot.Tasks.Single(task => task.Name == "Retired");
    var idle = snapshot.Tasks.Single(task => task.Name == "IDLE");
    Check(sensor.Address == snapshot.CurrentTaskAddress && sensor.Priority == 2 && sensor.BasePriority == 1 && sensor.RuntimeCounter == 100,
        "Current task address, inherited priority, base priority or runtime are incorrect.");
    Check(sensor.State == "Running" && receiver.State == "Blocked" && suspended.State == "Suspended" &&
          retired.State == "Deleted" && idle.State == "Ready", "Task states are not mapped from independent FreeRTOS list membership.");
    Check(new[] { sensor, receiver, suspended, retired, idle }.Select(task => task.StackHighWaterBytes)
          .SequenceEqual(new ulong?[] { 64, 96, 32, 16, 128 }), "Stack fill scanning must report bytes, rather than words or pointer distance.");
    Check(snapshot.Tasks.All(task => task.StackSizeBytes == 256), "Inclusive pxEndOfStack must yield the exact stack allocation byte size.");
    Check(snapshot.Heap is { TotalBytes: 4096, FreeBytes: 2304, MinimumEverFreeBytes: 1024, AllocationCount: 9, FreeCount: 3,
        LargestFreeBlockBytes: 1280, FreeBlockCount: 2 }, "Heap statistics differ from heap_4-style free list and globals in ELF.");
    Check(snapshot.Objects.Count == 3, "Vacant queue registry entries must be skipped; unique registered objects must be retained.");
    var queue = snapshot.Objects.Single(item => item.Name == "Messages");
    var mutex = snapshot.Objects.Single(item => item.Name == "SPI lock");
    var binary = snapshot.Objects.Single(item => item.Name == "Wakeup");
    Check(queue.Count == 3 && queue.Capacity == 8 && queue.ItemSize == 4 && queue.SendWaiters == 0 && queue.ReceiveWaiters == 1,
        "Queue occupancy, storage width and receiver wait count are incorrect.");
    Check(queue.Kind == "Queue" && mutex.Kind == "RecursiveMutex" && binary.Kind == "BinarySemaphore",
        "Object kind must come from the FreeRTOS trace field, rather than guesses from occupancy: " + string.Join(", ", snapshot.Objects.Select(item => item.Kind)));
    Check(mutex.MutexOwnerAddress == sensor.Address && mutex.RecursionCount == 2 && mutex.Count == 0 && mutex.Capacity == 1,
        "Recursive mutex owner or nesting differs from the initialized queue union.");
    Check(binary.Count == 1 && binary.Capacity == 1 && binary.ItemSize == 0 && binary.MutexOwnerAddress is null,
        "Binary semaphore must not be presented as a mutex owner.");
}

foreach (var (name, executable, elf) in new[]
{
    ("ARM", armGdb, armElf), ("WCH RISC-V", Path.Combine(wchRoot, "riscv-wch-elf-gdb.exe"), wchElf)
})
{
    var transport = new ElfMiTransport(executable);
    await using var adapter = new GdbDebugAdapter(transport);
    await InitializeAsync(adapter, elf);
    var first = transport.Commands.Count;
    var snapshot = await new FreeRtosInspector(adapter, stackGrowsDown: true).ReadAsync();
    CheckFixture(snapshot); CheckReadOnly(transport.Commands.Skip(first));
    var folder = name == "ARM" ? "arm" : "wch";
    await File.WriteAllTextAsync(Path.Combine(output, folder, "snapshot.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllLinesAsync(Path.Combine(output, folder, "mi-commands.log"), transport.Commands);
    await File.WriteAllLinesAsync(Path.Combine(output, folder, "mi-records.log"), transport.Records);
    Pass(name + " real GDB reads scheduler, five task states, byte watermarks, heap free blocks and three registered objects from local ELF only");
}

async Task<FreeRtosSnapshot> FaultSnapshotAsync(Func<string, string?> fault, IReadOnlyList<string>? handles = null, string? elf = null, bool rejectTraversal = false)
{
    var native = new ElfMiTransport(armGdb);
    var transport = new FaultMiTransport(native, fault);
    await using var adapter = new GdbDebugAdapter(transport);
    await InitializeAsync(adapter, elf ?? armElf);
    var first = transport.Commands.Count;
    var snapshot = await new FreeRtosInspector(adapter, stackGrowsDown: true).ReadAsync(handles);
    CheckReadOnly(transport.Commands.Skip(first));
    if (rejectTraversal)
        Check(transport.Commands.Skip(first).All(command => !Expression(command).Contains("->", StringComparison.Ordinal) &&
              !command.StartsWith("-data-read-memory-bytes ", StringComparison.Ordinal)),
            "An invalid kernel signature must not continue dereferencing task, heap or object pointers.");
    Check(transport.Commands.Count - first < 2000, "Broken target links must terminate in a bounded snapshot.");
    return snapshot;
}
string Error(string message) => "^error,msg=" + MiRecord.Quote(message);
string Value(ulong value) => "^done,value=" + MiRecord.Quote(value.ToString(CultureInfo.InvariantCulture));

var minimal = await FaultSnapshotAsync(_ => null, elf: minimalElf);
Check(minimal.IsAvailable && minimal.Tasks.Count == 5 && minimal.Tasks.All(task => task.BasePriority is null && task.RuntimeCounter is null &&
      task.StackSizeBytes is null && task.StackHighWaterBytes is null), "Disabled optional task fields must remain unknown, rather than become zero.");
Check(minimal.Heap is { AllocationCount: null, FreeCount: null, FreeBytes: 2304 }, "Missing allocator counters must not hide available heap usage.");
Check(minimal.Objects.Count == 3, "Queue registry inspection must work with configUSE_TRACE_FACILITY disabled.");
Pass("Real ELF without trace, runtime or stack-end fields retains partial values and never substitutes zero for missing data");

const string rawMemoryError = "Cannot access memory at address 0xdeadbeef — raw RTOS diagnostic";
var rawError = await FaultSnapshotAsync(command => Expression(command).Contains("xTickCount", StringComparison.Ordinal) ? Error(rawMemoryError) : null);
Check(rawError.TickCount is null && rawError.Diagnostics.Any(message => message.Contains(rawMemoryError, StringComparison.Ordinal)) && rawError.Tasks.Count == 5,
    "Raw GDB errors must be retained without discarding readable task and heap data.");
Pass("Target memory error is kept verbatim and only the failing Tick value is unavailable");

var missing = await FaultSnapshotAsync(command => command.StartsWith("-data-evaluate-expression ", StringComparison.Ordinal) &&
    Expression(command) != "(unsigned long long)(sizeof(void*))" ? Error("No symbol in current context.") : null);
Check(!missing.IsAvailable && missing.Tasks.Count == 0 && missing.Heap is null && missing.Objects.Count == 0 && missing.Diagnostics.Count > 0,
    "Bare or stripped ELF must report unsupported RTOS symbols, rather than a fabricated empty kernel.");
Pass("Missing RTOS symbols produce an explicit unavailable state without fabricated metrics");

var unregistered = await FaultSnapshotAsync(command => Expression(command).Contains("xQueueRegistry", StringComparison.Ordinal) ? Error("Registry disabled.") : null,
    ["externalQueue", "externalMutex"]);
Check(unregistered.Objects.Count == 2 && unregistered.Objects.Any(item => item.Count == 3 && item.Capacity == 8) &&
      unregistered.Objects.Any(item => item.MutexOwnerAddress == unregistered.CurrentTaskAddress), "Explicit handles must remain readable when the registry is disabled.");
Pass("Explicit global queue and mutex handles work when the queue registry is disabled");

var smp = await FaultSnapshotAsync(command => Expression(command).Contains("pxCurrentTCBs)", StringComparison.Ordinal) ? Value(8) : null);
Check(smp.IsAvailable && smp.Tasks.Count == 0 && smp.Diagnostics.Any(message => message.Contains("多核", StringComparison.Ordinal)),
    "Unsupported SMP must not be parsed as a classic single-current-TCB kernel.");
Pass("Detected SMP is reported explicitly and single-core task states are not fabricated");

var legacySmp = await FaultSnapshotAsync(command => Expression(command).Contains("sizeof(::pxCurrentTCB)", StringComparison.Ordinal) ? Value(8) : null);
Check(legacySmp.IsAvailable && legacySmp.Tasks.Count == 0 && legacySmp.Diagnostics.Any(message => message.Contains("SMP", StringComparison.Ordinal)),
    "A legacy pxCurrentTCB array cannot be mistaken for the classic scalar pointer.");
Pass("A legacy SMP current-TCB array is detected by pointer width and does not fabricate single-core task states");

var deduplicated = await FaultSnapshotAsync(_ => null, ["externalQueue", "externalQueue", "externalMutex"]);
Check(deduplicated.Objects.Count == 3, "Registered and explicit references to the same object must be deduplicated by target address.");
Pass("Registered and explicitly supplied handles are deduplicated by object identity");

async Task<ulong> FixtureAddressAsync(string expression)
{
    var native = new ElfMiTransport(armGdb);
    await using var adapter = new GdbDebugAdapter(native);
    await InitializeAsync(adapter, armElf);
    var response = await adapter.SendAsync("-data-evaluate-expression " + MiRecord.Quote("(unsigned long long)(" + expression + ")"));
    return ulong.Parse(response.String("value"), CultureInfo.InvariantCulture);
}
var delayedItem = await FixtureAddressAsync("&(task_b.xStateListItem)");
var nextItemExpression = $"((ListItem_t*)0x{delayedItem:x})->pxNext";
var badTaskLoop = await FaultSnapshotAsync(command =>
{
    var expression = Expression(command);
    if (expression.Contains("xDelayedTaskList1.uxNumberOfItems", StringComparison.Ordinal)) return Value(2);
    return expression.Contains(nextItemExpression, StringComparison.Ordinal) ? Value(delayedItem) : null;
});
Check(badTaskLoop.Tasks.Count == 5 && badTaskLoop.Diagnostics.Any(message => message.Contains("循环", StringComparison.Ordinal)),
    "A non-sentinel task list cycle must stop, report its diagnostic and keep the readable tasks.");
Pass("A non-sentinel task list cycle is detected without unbounded traversal or duplicate tasks");

var secondHeapBlock = await FixtureAddressAsync("&(ucHeap.links.second)");
var heapNextExpression = $"((BlockLink_t*)0x{secondHeapBlock:x})->pxNextFreeBlock";
var badHeapLoop = await FaultSnapshotAsync(command => Expression(command).Contains(heapNextExpression, StringComparison.Ordinal) ? Value(secondHeapBlock) : null);
Check(badHeapLoop.Heap is { FreeBytes: 2304, LargestFreeBlockBytes: null, FreeBlockCount: null } &&
      badHeapLoop.Diagnostics.Any(message => message.Contains("堆空闲", StringComparison.Ordinal)),
    "A cyclic heap list must hide unverified fragmentation metrics while keeping independent allocator globals.");
Pass("A cyclic heap list keeps free-byte globals but suppresses unverified free-block and fragmentation metrics");

var maintenanceTask = await FixtureAddressAsync("&task_c");
var receiveList = await FixtureAddressAsync("&(messages.xTasksWaitingToReceive)");
var eventContainerExpression = $"((TCB_t*)0x{maintenanceTask:x})->xEventListItem.pxContainer";
var infiniteWait = await FaultSnapshotAsync(command => Expression(command).Contains(eventContainerExpression, StringComparison.Ordinal) ? Value(receiveList) : null);
Check(infiniteWait.Tasks.Single(task => task.Name == "Maintenance").State == "Blocked", "Indefinite queue waits use the suspended list and must not be mistaken for explicit suspension.");
Pass("A task in the suspended list with an event wait is reported Blocked rather than explicitly suspended");

var countMismatch = await FaultSnapshotAsync(command => Expression(command).EndsWith("::uxCurrentNumberOfTasks)", StringComparison.Ordinal) ? Value(4) : null);
Check(countMismatch.ReportedTaskCount == 4 && countMismatch.Tasks.Count == 5 &&
      countMismatch.Diagnostics.Any(message => message.Contains("快照可能不完整", StringComparison.Ordinal)),
    "Reported-count discrepancies must remain visible rather than trimming the task list.");
Pass("An inconsistent kernel task count is retained and marks the snapshot incomplete");

foreach (var (name, field, corruptValue) in new[]
{
    ("scheduler flag", "::xSchedulerRunning)", 0xa5a5a5a5UL),
    ("reported task count", "::uxCurrentNumberOfTasks)", 1552988982UL),
    ("unaligned current task", "::pxCurrentTCB)", 182634987UL)
})
{
    var corrupt = await FaultSnapshotAsync(command => Expression(command).EndsWith(field, StringComparison.Ordinal) ? Value(corruptValue) : null,
        rejectTraversal: true);
    Check(!corrupt.IsAvailable && corrupt.SchedulerRunning is null && corrupt.ReportedTaskCount is null &&
          corrupt.CurrentTaskAddress is null && corrupt.Tasks.Count == 0 && corrupt.Heap is null && corrupt.Objects.Count == 0 &&
          corrupt.Diagnostics.Any(message => message.Contains(corruptValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                                            message.Contains("0x" + corruptValue.ToString("x", CultureInfo.InvariantCulture), StringComparison.Ordinal)),
        "Corrupt " + name + " must not be treated as a running kernel merely because ELF contains FreeRTOS symbols.");
}
Pass("Garbage scheduler flags, billion-scale task counts and invalid current pointers are rejected before target data traversal");

var conflictingEmptyCount = await FaultSnapshotAsync(command => Expression(command).EndsWith("::uxCurrentNumberOfTasks)", StringComparison.Ordinal) ? Value(0) : null,
    rejectTraversal: true);
Check(!conflictingEmptyCount.IsAvailable && conflictingEmptyCount.ReportedTaskCount is null,
    "A zero task count cannot be presented as a valid running kernel with a nonzero current TCB.");
var conflictingCurrent = await FaultSnapshotAsync(command => Expression(command).EndsWith("::pxCurrentTCB)", StringComparison.Ordinal) ? Value(0) : null,
    rejectTraversal: true);
Check(!conflictingCurrent.IsAvailable && conflictingCurrent.CurrentTaskAddress is null,
    "A running scheduler without a selected task must not be reported as valid.");
Pass("Contradictory running scheduler, current-TCB and task-count fields hide unverified metrics");

var unstarted = await FaultSnapshotAsync(command =>
{
    var expression = Expression(command);
    return expression.EndsWith("::uxCurrentNumberOfTasks)", StringComparison.Ordinal) ||
           expression.EndsWith("::pxCurrentTCB)", StringComparison.Ordinal) ||
           expression.EndsWith("::xSchedulerRunning)", StringComparison.Ordinal) ||
           expression.Contains(".uxNumberOfItems)", StringComparison.Ordinal) ? Value(0) : null;
});
Check(unstarted.IsAvailable && unstarted.SchedulerRunning == false && unstarted.ReportedTaskCount == 0 &&
      unstarted.CurrentTaskAddress == 0 && unstarted.Tasks.Count == 0,
    "An unstarted kernel's initialized zero fields are legal and must not be treated as corrupted RAM.");
Pass("A legal not-yet-started kernel with zero tasks and a null current TCB remains available");

var hugeSuspended = await FaultSnapshotAsync(command => Expression(command).EndsWith("::uxSchedulerSuspended)", StringComparison.Ordinal) ? Value(3695348878) : null);
Check(hugeSuspended.IsAvailable && hugeSuspended.SchedulerSuspended is null && hugeSuspended.Tasks.Count == 5,
    "An implausible suspend nesting count must become unknown while preserving independently readable task state.");
Pass("Implausible suspend nesting is not displayed as a trustworthy counter");

var oversizedHeap = await FaultSnapshotAsync(command => Expression(command).EndsWith("::xFreeBytesRemaining)", StringComparison.Ordinal) ? Value(2792760398) : null);
Check(oversizedHeap.IsAvailable && oversizedHeap.Tasks.Count == 5 && oversizedHeap.Heap is
    { TotalBytes: 4096, FreeBytes: null, MinimumEverFreeBytes: null, LargestFreeBlockBytes: null, FreeBlockCount: null },
    "A heap free-byte value above the DWARF array capacity must not be shown as a valid usage metric.");
Pass("Heap free bytes above ucHeap capacity are hidden instead of being shown as billion-byte usage");

foreach (var badMinimum in new[] { 3089365048UL, 3072UL })
{
    var inconsistentHeap = await FaultSnapshotAsync(command => Expression(command).EndsWith("::xMinimumEverFreeBytesRemaining)", StringComparison.Ordinal) ? Value(badMinimum) : null);
    Check(inconsistentHeap.IsAvailable && inconsistentHeap.Heap is { FreeBytes: 2304, MinimumEverFreeBytes: null },
        "An impossible minimum-free value must remain unknown while preserving readable current free bytes.");
}
Pass("Invalid historical minimum heap values remain unknown while valid current usage is preserved");

var uninitializedHeap = await FaultSnapshotAsync(command => Expression(command).EndsWith("::pxEnd)", StringComparison.Ordinal) ? Value(0) : null);
Check(uninitializedHeap.IsAvailable && uninitializedHeap.Heap is { TotalBytes: 4096, FreeBytes: null, MinimumEverFreeBytes: null },
    "An uninitialized heap must not convert its zero counters into a fabricated usage percentage.");
Pass("Uninitialized heap counters are marked unavailable without changing valid task availability");

var truncatedNative = new ElfMiTransport(armGdb);
var truncatedMemory = new FaultMiTransport(truncatedNative, command => command.StartsWith("-data-read-memory-bytes ", StringComparison.Ordinal)
    ? "^done,memory=[{begin=\"0x20000000\",offset=\"0x0\",end=\"0x20000001\",contents=\"a5\"}]" : null);
await using (var adapter = new GdbDebugAdapter(truncatedMemory))
{
    await InitializeAsync(adapter, armElf);
    try { await new FreeRtosInspector(adapter, true).ReadAsync(); throw new InvalidOperationException("Expected GDB_PROTOCOL for truncated stack memory."); }
    catch (StudioXException exception) when (exception.Code == "GDB_PROTOCOL") { }
}
Pass("Truncated stack memory replies fail with GDB_PROTOCOL and cannot produce a false watermark");

{
    var native = new ElfMiTransport(armGdb);
    var delayed = new DelayedMiTransport(native);
    await using var adapter = new GdbDebugAdapter(delayed);
    await InitializeAsync(adapter, armElf);
    var first = delayed.Commands.Count;
    delayed.Enabled = true;
    using var cancellation = new CancellationTokenSource();
    var read = new FreeRtosInspector(adapter, true).ReadAsync(token: cancellation.Token);
    await delayed.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    Check(delayed.HeldToken is { CanBeCanceled: false }, "In-flight RTOS MI must not receive user cancellation and fault the hardware transport.");
    Check(!read.IsCompleted, "Cancellation must wait for the pending MI response rather than leave a response outstanding.");
    delayed.CompleteRead();
    try { await read; throw new InvalidOperationException("Expected active RTOS cancellation."); }
    catch (OperationCanceledException) { }
    Check(delayed.Commands.Count == first + 1 && !delayed.Disposed,
        "Cancellation after the first completed read must not send another MI command or dispose the transport.");
    var fresh = await new FreeRtosInspector(adapter, true).ReadAsync();
    CheckFixture(fresh); CheckReadOnly(delayed.Commands.Skip(first));
    Pass("Cancellation during pending MI drains the read, issues no next command and leaves the same adapter usable for a fresh snapshot");
}

{
    var native = new ElfMiTransport(armGdb);
    var malformed = new FaultMiTransport(native, command => command.StartsWith("-data-evaluate-expression ", StringComparison.Ordinal)
        ? "^done,value=\"4\",value=\"8\"" : null);
    await using var adapter = new GdbDebugAdapter(malformed);
    await InitializeAsync(adapter, armElf);
    try { await new FreeRtosInspector(adapter).ReadAsync(); throw new InvalidOperationException("Expected GDB_PROTOCOL for duplicate value."); }
    catch (StudioXException exception) when (exception.Code == "GDB_PROTOCOL") { }
    Pass("Ambiguous MI numeric fields fail with GDB_PROTOCOL");
}

{
    var native = new ElfMiTransport(armGdb);
    await using var adapter = new GdbDebugAdapter(native);
    await InitializeAsync(adapter, armElf);
    var first = native.Commands.Count;
    foreach (var invalid in new[] { "erase()", "externalQueue[0]", "externalQueue=0", "$pc", "0x20000000", "x;quit", "x\nquit" })
    {
        try { await new FreeRtosInspector(adapter).ReadAsync([invalid]); throw new InvalidOperationException("Expected invalid object symbol rejection."); }
        catch (ArgumentException) { }
    }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await new FreeRtosInspector(adapter).ReadAsync(token: cancellation.Token); throw new InvalidOperationException("Expected canceled read."); }
    catch (OperationCanceledException) { }
    Check(native.Commands.Count == first, "Invalid handles and pre-cancellation must issue no MI command.");
    Pass("Function calls, assignment, indexing and command injection are rejected before MI; cancellation also sends nothing");
}

await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
{
    hardwareAccess = false, inferiorExecuted = false, fixtureKind = "Initialized local ELF data with FreeRTOS V10.4.6 field definitions",
    passed = passed.Count, checks = passed
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS — {passed.Count} RTOS offline checks; no probe, OpenOCD, target connection or inferior execution");
return 0;
