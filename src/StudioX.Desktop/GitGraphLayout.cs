namespace StudioX.Desktop;

/// <summary>由提交和父提交的拓扑关系生成稳定的列轨道；输入须按 git log 从新到旧排列。</summary>
public static class GitGraphLayout
{
    private sealed record Track(string TargetHash, int ColorIndex);

    public static IReadOnlyList<GitGraphRow> Build(IReadOnlyList<(string Hash, IReadOnlyList<string> Parents)> commits)
        => Build(commits.Select(commit => new GitGraphCommit(commit.Hash, commit.Parents)));

    public static IReadOnlyList<GitGraphRow> Build(IEnumerable<GitGraphCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var ordered = commits.ToArray();
        var remaining = new HashSet<string>(ordered.Select(commit => commit.Hash), StringComparer.OrdinalIgnoreCase);
        var pending = new List<Track>();
        var rows = new List<GitGraphRow>(ordered.Length);
        var nextColor = 0;
        var greatestLaneCount = 1;

        foreach (var commit in ordered)
        {
            if (string.IsNullOrWhiteSpace(commit.Hash))
            {
                throw new ArgumentException("提交哈希不能为空。", nameof(commits));
            }
            remaining.Remove(commit.Hash);

            var incoming = pending.ToArray();
            var matches = Enumerable.Range(0, incoming.Length)
                .Where(index => string.Equals(incoming[index].TargetHash, commit.Hash, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // 不在现有轨道的提交是另一个分支的头；放在当前轨道右侧，不扰动已有分支。
            var nodeLane = matches.Length == 0 ? incoming.Length : matches[0];
            var nodeColor = matches.Length == 0 ? nextColor++ : incoming[nodeLane].ColorIndex;
            var segments = new List<GitGraphSegment>();

            foreach (var index in matches)
            {
                segments.Add(new(index, 0, nodeLane, 0.5, incoming[index].ColorIndex));
            }

            var matched = matches.ToHashSet();
            var outgoing = Enumerable.Range(0, incoming.Length)
                .Where(index => !matched.Contains(index))
                .Select(index => incoming[index]).ToList();

            // 第一父提交继承当前轨道，其余父提交开启并行轨道；已存在的父轨道直接汇入。
            var insertAt = Math.Min(nodeLane, outgoing.Count);
            var parentTracks = new List<Track>();
            foreach (var parentHash in (commit.Parents ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(parentHash))
                {
                    continue;
                }
                // 搜索或分支筛选可能隐去父提交；不为不可见提交保留永久悬空的轨道。
                if (!remaining.Contains(parentHash))
                {
                    continue;
                }
                var track = outgoing.FirstOrDefault(candidate =>
                    string.Equals(candidate.TargetHash, parentHash, StringComparison.OrdinalIgnoreCase));
                if (track is null)
                {
                    track = new Track(parentHash, parentTracks.Count == 0 ? nodeColor : nextColor++);
                    outgoing.Insert(insertAt, track);
                    insertAt++;
                }
                else if (parentTracks.Count == 0)
                {
                    // 第一父提交已经占有轨道时，让后续父轨道接在其右侧。
                    insertAt = outgoing.IndexOf(track) + 1;
                }
                parentTracks.Add(track);
            }

            for (var oldLane = 0; oldLane < incoming.Length; oldLane++)
            {
                if (matched.Contains(oldLane))
                {
                    continue;
                }
                var newLane = outgoing.IndexOf(incoming[oldLane]);
                segments.Add(new(oldLane, 0, newLane, 1, incoming[oldLane].ColorIndex));
            }
            foreach (var track in parentTracks)
            {
                segments.Add(new(nodeLane, 0.5, outgoing.IndexOf(track), 1, track.ColorIndex));
            }

            var laneCount = Math.Max(Math.Max(incoming.Length, outgoing.Count), nodeLane + 1);
            greatestLaneCount = Math.Max(greatestLaneCount, laneCount);
            rows.Add(new GitGraphRow(commit.Hash, nodeLane, nodeColor,
                (commit.Parents?.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0) > 1,
                segments.ToArray(), laneCount, 0));
            pending = outgoing;
        }

        // 所有行使用相同的列宽，避免分支数量变化时轨道在相邻行错位。
        return rows.Select(row => row with { TotalLaneCount = greatestLaneCount }).ToArray();
    }
}
