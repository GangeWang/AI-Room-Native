import Foundation

// MARK: Project room extras, after upstream AIROOM's project flow:
// - tasks whose files don't overlap run side by side (one task per member);
// - an author may dispute review findings on a fix; the owner rules on each;
// - a member whose quota ran out waits with a reset time. Nothing is re-sent
//   by itself: the owner presses 接著做 or hands the work to another member.

/// One review finding, with an id the author can dispute.
public struct FlowFinding: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var severity: String
    public var location: String
    public var request: String
    public init(id: String, severity: String, location: String, request: String) {
        self.id = id; self.severity = String(severity.prefix(20)); self.location = String(location.prefix(300))
        self.request = String(request.prefix(1_500))
    }
    public var line: String { "[\(id)][\(severity)] \(location.isEmpty ? "" : location + "：")\(request)" }
}

public struct FlowDispute: Codable, Equatable, Sendable {
    public var findingID: String
    public var reason: String
    /// What the lead said when they couldn't settle it and handed it to the owner.
    public var leadNote: String?
    public init(findingID: String, reason: String, leadNote: String? = nil) {
        self.findingID = findingID; self.reason = String(reason.prefix(1_500)); self.leadNote = leadNote.map { String($0.prefix(1_500)) }
    }
}

public enum FlowVerdict: String, Codable, Sendable {
    /// 支持作者: the finding is dropped. 維持複核: the author must fix it.
    case author, review
}

public struct FlowRuling: Codable, Equatable, Sendable {
    public var finding: FlowFinding
    /// The author's reason for disputing.
    public var reason: String
    public var verdict: FlowVerdict
    public var at: Date
    /// The lead who ruled; nil when the owner did.
    public var judge: MemberID?
    /// The lead's grounds for the ruling.
    public var note: String?
    public init(finding: FlowFinding, reason: String, verdict: FlowVerdict, at: Date = .now, judge: MemberID? = nil, note: String? = nil) {
        self.finding = finding; self.reason = reason; self.verdict = verdict; self.at = at
        self.judge = judge; self.note = note.map { String($0.prefix(1_500)) }
    }
}

/// The lead's answer on one dispute; `nil` verdict means unsure (the owner decides).
public struct LeadRuling: Equatable, Sendable {
    public var findingID: String
    public var verdict: FlowVerdict?
    public var reason: String
}

public struct FlowReview: Equatable, Sendable {
    public var pass: Bool
    public var summary: String
    public var findings: [FlowFinding]
    public var text: String { findings.isEmpty ? summary : findings.map { "- " + $0.line }.joined(separator: "\n") }
}

/// A member whose quota ran out while working on this flow.
public struct FlowQuotaWait: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID
    public var agent: MemberID
    /// What was being done (e.g. 執行任務「…」).
    public var stage: String
    public var since: Date
    /// When the official quota says it resets, if known.
    public var resetAt: Date?
    /// The reset time passed (or a fresh quota sample shows room); the owner was told.
    public var ready: Bool
    public var message: String
    public init(id: UUID = UUID(), agent: MemberID, stage: String, since: Date = .now, resetAt: Date? = nil, message: String) {
        self.id = id; self.agent = agent; self.stage = String(stage.prefix(200)); self.since = since
        self.resetAt = resetAt; ready = false
        // The `|<unix seconds>` reset hint is read into resetAt; it isn't for reading.
        self.message = String(message.replacingOccurrences(of: #"\|\d{9,}"#, with: "", options: .regularExpression).prefix(600))
    }
}

public enum QuotaSignal {
    /// Whether a failed run says the member's usage limit was reached.
    public static func isQuotaFailure(_ message: String) -> Bool {
        let text = message.lowercased()
        return ["usage limit", "usagelimitexceeded", "rate limit", "rate_limit", "limit reached", "hit your limit",
                "hit your usage", "session limit", "weekly limit", "quota exceeded", "額度已用完", "额度已用完"]
            .contains { text.contains($0) }
    }

    /// A reset time some CLIs append as `…|<unix seconds>`.
    public static func resetHint(_ message: String, now: Date = .now) -> Date? {
        guard let bar = message.lastIndex(of: "|") else { return nil }
        let digits = message[message.index(after: bar)...].prefix { $0.isNumber }
        guard digits.count >= 9, let seconds = Double(digits) else { return nil }
        let date = Date(timeIntervalSince1970: seconds > 1e12 ? seconds / 1000 : seconds)
        return date > now && date < now.addingTimeInterval(14 * 86_400) ? date : nil
    }

    /// The latest future reset among the windows that are used up.
    public static func resetAt(_ quota: AccountQuota?, now: Date = .now) -> Date? {
        quota?.windows.filter(\.exhausted).compactMap(\.resetsAt).filter { $0 > now }.max()
    }

    /// A fresh official sample says this member has no quota left (and that window hasn't reset since).
    public static func blocks(_ quota: AccountQuota?, now: Date = .now) -> Bool {
        guard let quota, quota.status == .ready, quota.isFresh(at: now) else { return false }
        return quota.windows.contains { $0.exhausted && ($0.resetsAt.map { $0 > now } ?? true) }
    }

    /// The reset time passed, or a sample taken after the wait began shows room again.
    public static func recovered(_ wait: FlowQuotaWait, quota: AccountQuota?, now: Date = .now) -> Bool {
        if let reset = wait.resetAt, now >= reset { return true }
        guard let quota, quota.status == .ready, quota.checkedAt > wait.since, quota.isFresh(at: now) else { return false }
        return !quota.windows.contains(where: \.exhausted)
    }
}

extension ProjectFlowEngine {
    public static let maxWaivers = 2

    /// Relative, cleaned paths; "." or "" stands for the whole project.
    public static func normalizedPaths(_ paths: [String]) -> [String] {
        var seen: [String] = []
        for raw in paths.prefix(20) {
            var path = raw.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "\\", with: "/")
            while path.hasPrefix("./") { path.removeFirst(2) }
            while path.hasPrefix("/") { path.removeFirst() }
            while path.hasSuffix("/") { path.removeLast() }
            if path.isEmpty { path = "." }
            path = String(path.prefix(300))
            if !seen.contains(path) { seen.append(path) }
        }
        return seen
    }

    /// Two file lists may touch the same file. An empty list means "unknown", which overlaps everything.
    public static func filesOverlap(_ a: [String], _ b: [String]) -> Bool {
        guard !a.isEmpty, !b.isEmpty else { return true }
        for x in a {
            for y in b where x == "." || y == "." || x == y || x.hasPrefix(y + "/") || y.hasPrefix(x + "/") { return true }
        }
        return false
    }

    /// Tasks that can start now next to `active`: their dependencies are done, their member
    /// isn't busy or out of quota, and their files don't overlap anything running.
    public static func readyTasks(in round: FlowRound, plan: FlowPlan, active: Set<UUID>, maxParallel: Int,
                                  unavailable: Set<MemberID> = []) -> [FlowTask] {
        let done = Set(plan.tasks.filter { $0.status == .done }.map(\.key))
        var running = plan.tasks.filter { active.contains($0.id) }
        var picked: [FlowTask] = []
        for task in plan.tasks(in: round) where [.pending, .fixing, .reviewing].contains(task.status) && !active.contains(task.id) {
            guard running.count < min(3, max(1, maxParallel)) else { break }
            // A task waiting for review needs its reviewer, not its author.
            let worker = task.status == .reviewing ? (task.reviewer ?? task.member) : task.member
            guard task.dependencies.allSatisfy(done.contains), !unavailable.contains(worker),
                  !running.contains(where: { $0.member == task.member }),
                  running.allSatisfy({ !filesOverlap($0.files, task.files) }) else { continue }
            picked.append(task)
            running.append(task)
        }
        return picked
    }

    /// Reads a reviewer's JSON verdict; findings get ids f1, f2, … for disputes.
    public static func parseReview(_ text: String) -> FlowReview {
        guard let object = LoungeEngine.jsonObject(in: text) else {
            let lowered = text.lowercased()
            return FlowReview(pass: lowered.contains("verdict: pass") || text.contains("通過"), summary: text, findings: [])
        }
        let verdict = ((object["verdict"] as? String) ?? "").lowercased()
        var findings: [FlowFinding] = []
        for item in ((object["findings"] as? [Any]) ?? []).prefix(30) {
            let id = "f\(findings.count + 1)"
            if let line = item as? String {
                findings.append(FlowFinding(id: id, severity: "should-fix", location: "", request: line))
            } else if let dict = item as? [String: Any] {
                findings.append(FlowFinding(id: id, severity: (dict["severity"] as? String) ?? "should-fix",
                                            location: (dict["location"] as? String) ?? "",
                                            request: (dict["request"] as? String) ?? (dict["text"] as? String) ?? ""))
            }
        }
        return FlowReview(pass: verdict == "pass", summary: (object["summary"] as? String) ?? "", findings: findings)
    }

    static let disputeMarker = "AIROOM_DISPUTES:"

    /// Splits an author's fix report from the disputes line at its end. Only open,
    /// not-yet-ruled findings can be disputed, each once.
    public static func parseDisputes(_ text: String, open: [FlowFinding], ruled: [FlowRuling]) -> (summary: String, disputes: [FlowDispute]) {
        guard let range = text.range(of: disputeMarker, options: .backwards) else { return (text, []) }
        let summary = String(text[..<range.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
        guard let object = LoungeEngine.jsonObject(in: String(text[range.upperBound...])) else { return (summary, []) }
        let allowed = Set(open.map(\.id)).subtracting(ruled.map(\.finding.id))
        var disputes: [FlowDispute] = []
        for item in ((object["disputes"] as? [[String: Any]]) ?? []) {
            let id = ((item["finding"] as? String) ?? (item["findingId"] as? String) ?? "").trimmingCharacters(in: .whitespaces)
            let reason = ((item["reason"] as? String) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard allowed.contains(id), !reason.isEmpty, !disputes.contains(where: { $0.findingID == id }) else { continue }
            disputes.append(FlowDispute(findingID: id, reason: reason))
        }
        return (summary, disputes)
    }

    /// Applies the owner's rulings. A ruling for the author gives one return back (at most
    /// `maxWaivers` per task); findings upheld for the review go back to the author to fix.
    public static func applyRulings(_ task: inout FlowTask, verdicts: [String: FlowVerdict], judge: MemberID? = nil,
                                    notes: [String: String] = [:], at now: Date = .now) {
        let disputed = task.disputes.filter { verdicts[$0.findingID] != nil }
        guard !disputed.isEmpty, disputed.count == task.disputes.count else { return }
        var upheld: [FlowFinding] = []
        var forAuthor = false
        for dispute in disputed {
            guard let finding = task.openFindings.first(where: { $0.id == dispute.findingID }),
                  let verdict = verdicts[dispute.findingID] else { continue }
            task.rulings.append(FlowRuling(finding: finding, reason: dispute.reason, verdict: verdict, at: now,
                                           judge: judge, note: notes[dispute.findingID]))
            if verdict == .review { upheld.append(finding) } else { forAuthor = true }
        }
        if forAuthor, task.returns > 0, task.waivers < maxWaivers { task.returns -= 1; task.waivers += 1 }
        task.disputes = []
        if upheld.isEmpty {
            task.openFindings = []
            task.findings = ""
            task.status = .reviewing
        } else {
            task.openFindings = upheld
            task.findings = upheld.map { "- " + $0.line }.joined(separator: "\n")
            task.status = .fixing
        }
    }
}

// MARK: The lead rules on disputes (three or more members)

extension ProjectFlowEngine {
    /// The lead may rule only when they are neither the author nor the reviewer.
    public static func leadCanRule(_ task: FlowTask, lead: MemberID?) -> Bool {
        guard let lead else { return false }
        return lead != task.member && lead != (task.reviewer ?? task.member)
    }

    public static func adjudicationPrompt(_ task: FlowTask, lead: MemberID, names: [MemberID: String], ownerName: String = "使用者",
                                          round: FlowRound? = nil, persona: String = "") -> String {
        func name(_ id: MemberID) -> String { names[id] ?? id.rawValue }
        let reviewer = task.reviewer.map(name) ?? "複核者"
        let notes = [round.map(\.startNote).flatMap { $0.isEmpty ? nil : "\(ownerName)開始這一輪時的附言：\($0)" },
                     round.map(\.userOpinion).flatMap { $0.isEmpty ? nil : "\(ownerName)上次驗收的意見：\($0)" }]
            .compactMap { $0 }.joined(separator: "\n")
        let items = task.disputes.compactMap { dispute -> String? in
            guard let finding = task.openFindings.first(where: { $0.id == dispute.findingID }) else { return nil }
            return "- \(finding.line)\n  作者的理由：\(dispute.reason)"
        }.joined(separator: "\n")
        return """
        你是 \(name(lead))，AIROOM 專案的負責人。\(name(task.member)) 修正任務「\(task.title)」時，對 \(reviewer) 的部分複核意見提出異議，請你逐條裁定。目前資料夾是作者修正後的副本，只讀查看，不要修改檔案。
        \(persona)
        任務目標：\(task.goal)
        完成標準：\n\(task.criteria.map { "- \($0)" }.joined(separator: "\n"))
        變更的檔案：\(task.changes.map { "\($0.kind.rawValue) \($0.path)" }.joined(separator: "、"))
        作者這次的說明：\(String(task.summary.prefix(3_000)))
        \(notes)
        有異議的複核意見：
        \(items)
        實際查看檔案，對照任務目標與完成標準核實每一條。只回傳一個 JSON 物件（\(LoungeEngine.quoteRule)）：
        {"rulings":[{"finding":"f1","verdict":"author 或 review 或 unsure","reason":"你核實到的依據"}]}
        author：異議成立，這條不用改；review：複核意見成立，作者必須修正；unsure：查不清楚或需要\(ownerName)取捨，交給\(ownerName)決定。每一條都要裁定，不要因為誰的語氣比較肯定就偏向誰；只裁定問題，不要修改檔案。
        """
    }

    /// Reads the lead's rulings. Nil unless every dispute got exactly one ruling.
    public static func parseAdjudication(_ text: String, disputes: [FlowDispute]) -> [LeadRuling]? {
        guard let object = LoungeEngine.jsonObject(in: text), let items = object["rulings"] as? [[String: Any]] else { return nil }
        let wanted = Set(disputes.map(\.findingID))
        var rulings: [LeadRuling] = []
        for item in items {
            let id = ((item["finding"] as? String) ?? (item["findingId"] as? String) ?? "").trimmingCharacters(in: .whitespaces)
            guard wanted.contains(id), !rulings.contains(where: { $0.findingID == id }) else { continue }
            let word = ((item["verdict"] as? String) ?? "").lowercased().trimmingCharacters(in: .whitespaces)
            let verdict: FlowVerdict? = ["author", "uphold-author"].contains(word) ? .author
                : ["review", "uphold-review"].contains(word) ? .review : nil
            let reason = ((item["reason"] as? String) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            rulings.append(LeadRuling(findingID: id, verdict: verdict, reason: String(reason.prefix(1_500))))
        }
        return rulings.count == wanted.count ? rulings : nil
    }
}

// MARK: Meeting-room record (the original posts the plan, decisions and round reports into the room)

extension ProjectFlowEngine {
    static func clip(_ text: String, _ limit: Int) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.count > limit ? String(trimmed.prefix(limit)) + "……" : trimmed
    }

    public static func planCard(_ plan: FlowPlan) -> String {
        var parts = ["【方案 · 第 \(plan.version) 版】", clip(plan.objective, 600)]
        if !plan.scope.isEmpty { parts.append("範圍：" + clip(plan.scope, 400)) }
        if !plan.deliverables.isEmpty { parts.append("交付：" + clip(plan.deliverables.joined(separator: "、"), 400)) }
        if !plan.acceptance.isEmpty { parts.append("驗收標準：" + clip(plan.acceptance.joined(separator: "；"), 400)) }
        parts.append("\(plan.rounds.count) 輪，\(plan.tasks.count) 項任務")
        return parts.joined(separator: "\n\n")
    }

    public static func startCard(version: Int, level: ReviewLevel, round: FlowRound, note: String) -> String {
        var text = "【開工 · \(round.title)】確認方案第 \(version) 版，\(level.title)。"
        if !note.isEmpty { text += "\n附言：" + clip(note, 600) }
        return text
    }

    public static func roundReportCard(_ round: FlowRound) -> String {
        "【\(round.title) · 輪次匯報】\n\n" + clip(round.report, 2_500)
    }

    public static func acceptanceCard(_ round: FlowRound, accepted: Bool, opinion: String = "") -> String {
        accepted ? "【驗收 · \(round.title)】通過，已寫回專案資料夾。"
                 : "【驗收 · \(round.title)】駁回：" + clip(opinion, 1_000)
    }

    public static func rulingCard(_ task: FlowTask, rulings: [FlowRuling], names: [MemberID: String] = [:]) -> String {
        let lines = rulings.map { ruling in
            "- \(ruling.verdict == .author ? "支持作者" : "維持複核")：\(clip(ruling.finding.request, 200))"
                + (ruling.note.map { "\n  理由：" + clip($0, 300) } ?? "")
        }
        let judge = rulings.first?.judge.map { "（負責人 \(names[$0] ?? $0.rawValue) 裁定）" } ?? ""
        return "【裁定 · \(task.title)】\(judge)\n" + lines.joined(separator: "\n")
    }

    public static func completionCard(_ summary: String) -> String {
        "【整段工作總結】\n\n" + clip(summary, 2_500)
    }
}
