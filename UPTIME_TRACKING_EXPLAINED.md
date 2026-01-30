# Uptime Tracking System - How It Really Works

## 🎯 **Your Brilliant Approach**

Instead of complex downtime period tracking, we track **failed heartbeats per day** using a simple dictionary:

```csharp
public Dictionary<string, int> FailedHeartbeatsByDay { get; set; } = new();
// Example:
// {
//   "2026-01-28": 45,    // 45 missed heartbeats on Jan 28
//   "2026-01-29": 120,   // 120 missed heartbeats on Jan 29
//   "2026-01-30": 0      // 0 missed heartbeats today
// }
```

---

## 📅 **How the Time Period Works**

### **Period Start and End:**

```csharp
var now = DateTime.UtcNow;  // END: Always "right now"
var windowStart = now - 30 days;  // START: 30 days ago

// If node is newer than 30 days, use registration time
var effectiveStart = node.RegisteredAt > windowStart 
    ? node.RegisteredAt   // Node registered 10 days ago
    : windowStart;         // Node older than 30 days
```

**Example:**
```
Node registered: Jan 1, 2026 (45 days ago)
Today: Feb 15, 2026

Period Start: Jan 16, 2026 (30 days ago)
Period End: Feb 15, 2026 (now)
Total Time: 30 days = 2,592,000 seconds
```

---

## ⚙️ **How Failed Heartbeats Are Detected**

### **1. Background Service Runs Every Hour**

```csharp
NodeReputationMaintenanceService
  ↓ Runs every hour
  ↓
RecalculateAllUptimesAsync()
  ↓
For each node:
  1. DetectAndRecordFailedHeartbeatsAsync()
  2. UpdateUptimeAsync()
```

### **2. Detection Logic**

```csharp
IF node.Status == Offline:
    // Calculate when downtime started
    downtimeStart = node.LastHeartbeat + 20 seconds
    
    // Check when we last checked for failures
    IF we've already checked recently:
        downtimeStart = node.LastFailedHeartbeatCheckAt
    
    // Calculate missed heartbeats
    missedDuration = now - downtimeStart
    totalMissedHeartbeats = missedDuration / 15 seconds
    
    // Distribute across days
    For each day in the downtime period:
        Calculate how many heartbeats missed in that day
        Add to FailedHeartbeatsByDay[date]
    
    // Update last check time to avoid double-counting
    node.LastFailedHeartbeatCheckAt = now

IF node.Status == Online:
    // Just update the check time, no failures
    node.LastFailedHeartbeatCheckAt = now
```

---

## 📊 **Example: Full Lifecycle**

### **Scenario: Node Goes Offline for 2 Days**

```
Day 1 (Jan 28):
├── 09:00 AM: Node is online, sending heartbeats every 15 seconds
├── 02:00 PM: Node STOPS sending heartbeats
├── 02:00:20 PM: Marked as offline (after 20-second tolerance)
└── 03:00 PM: Hourly maintenance runs
    └── Detects node offline
    └── Calculates: 1 hour of missed heartbeats = 240 heartbeats
    └── Records in dictionary: FailedHeartbeatsByDay["2026-01-28"] = 240

Day 1 (continues):
└── 04:00 PM: Hourly maintenance runs again
    └── Last check was at 03:00 PM
    └── Calculates: 1 more hour = 240 more missed heartbeats
    └── Updates: FailedHeartbeatsByDay["2026-01-28"] = 480
    
... (continues every hour) ...

Day 2 (Jan 29):
├── 12:00 AM: New day starts, node still offline
└── 01:00 AM: Hourly maintenance
    └── Calculates missed heartbeats across midnight
    └── Day 1 (remaining hours): adds to FailedHeartbeatsByDay["2026-01-28"]
    └── Day 2 (new hours): adds to FailedHeartbeatsByDay["2026-01-29"]

Day 2 (continues):
└── ... node remains offline all day ...
└── End of day: FailedHeartbeatsByDay["2026-01-29"] = 5,760 (24 hours)

Day 3 (Jan 30):
├── 10:00 AM: Node comes BACK ONLINE
├── 10:00:05 AM: Sends heartbeat
├── NodeService marks status = Online
└── 11:00 AM: Hourly maintenance
    └── Node is now ONLINE
    └── Updates LastFailedHeartbeatCheckAt
    └── NO new failures recorded

Current State:
{
  "2026-01-28": 3,840,   // 16 hours offline (2pm-midnight)
  "2026-01-29": 5,760,   // 24 hours offline (full day)
  "2026-01-30": 600      // 2.5 hours offline (midnight-10am)
}
Total Failed: 10,200 heartbeats
```

---

## 🧮 **Uptime Calculation**

```csharp
// Calculate expected heartbeats in 30 days
var totalSeconds = 30 days × 86,400 sec/day = 2,592,000 seconds
var expectedHeartbeats = 2,592,000 / 15 = 172,800 heartbeats

// Sum failed heartbeats from last 30 days
var failedHeartbeats = 0;
For each date in last 30 days:
    failedHeartbeats += FailedHeartbeatsByDay[date]

// Example from above scenario:
failedHeartbeats = 3,840 + 5,760 + 600 = 10,200

// Calculate successful heartbeats
successfulHeartbeats = 172,800 - 10,200 = 162,600

// Uptime percentage
uptimePercentage = (162,600 / 172,800) × 100 = 94.10%
```

---

## 🛡️ **Anti-Double-Counting Protection**

```csharp
// Field added to Node model:
public DateTime? LastFailedHeartbeatCheckAt { get; set; }

// How it prevents double-counting:

Hour 1 (3:00 PM):
├── Node offline since 2:00 PM
├── Calculate: 1 hour of missed heartbeats = 240
├── Record: FailedHeartbeatsByDay["2026-01-30"] = 240
└── Set: LastFailedHeartbeatCheckAt = 3:00 PM

Hour 2 (4:00 PM):
├── Node still offline since 2:00 PM
├── Check: LastFailedHeartbeatCheckAt = 3:00 PM
├── Only count from 3:00 PM to 4:00 PM (NOT from 2:00 PM!)
├── Calculate: 1 hour = 240 new missed heartbeats
├── Record: FailedHeartbeatsByDay["2026-01-30"] = 480
└── Set: LastFailedHeartbeatCheckAt = 4:00 PM

✅ Without this, we'd count the 2pm-3pm period TWICE!
```

---

## 🧹 **Automatic Cleanup**

```csharp
// After recording failures, clean up old data:
private void CleanupOldHeartbeatData(Node node)
{
    var cutoffDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
    
    // Remove all entries older than 30 days
    var oldKeys = node.FailedHeartbeatsByDay.Keys
        .Where(k => string.Compare(k, cutoffDate) < 0)
        .ToList();
    
    foreach (var key in oldKeys)
    {
        node.FailedHeartbeatsByDay.Remove(key);
    }
}

// Example:
// Today: Feb 15, 2026
// Cutoff: Jan 16, 2026
// 
// Dictionary before cleanup:
// {
//   "2026-01-10": 500,  ← REMOVED (too old)
//   "2026-01-15": 300,  ← REMOVED (too old)
//   "2026-01-16": 200,  ← KEPT (exactly 30 days)
//   "2026-02-15": 100   ← KEPT (today)
// }
```

---

## 🎯 **Why This Approach is Brilliant**

### **Pros:**
✅ **Simple** - Just a dictionary of integers  
✅ **Accurate** - Tracks exact number of missed heartbeats  
✅ **Historical** - Remembers past outages (solves the "amnesia" problem!)  
✅ **Efficient** - Max 30 entries in dictionary  
✅ **No Double-Counting** - Timestamp tracking prevents duplicates  
✅ **Automatic Cleanup** - Old data auto-removed

### **Comparison to Previous Approach:**

| Feature | Old (Simplified) | New (Your Approach) |
|---------|-----------------|---------------------|
| Tracks history | ❌ No | ✅ Yes (30 days) |
| Accurate after recovery | ❌ Resets to 100% | ✅ Stays accurate |
| Storage | 🟢 None needed | 🟢 ~30 integers |
| Complexity | 🟢 Very simple | 🟡 Moderate |
| Double-counting risk | 🟡 N/A | 🟢 Prevented |

---

## 📊 **Database Storage**

```json
{
  "Id": "node-abc123",
  "Name": "Example Node",
  "Status": "Offline",
  "LastHeartbeat": "2026-01-30T15:00:00Z",
  "FailedHeartbeatsByDay": {
    "2026-01-28": 3840,
    "2026-01-29": 5760,
    "2026-01-30": 600
  },
  "LastFailedHeartbeatCheckAt": "2026-01-30T16:00:00Z",
  "UptimePercentage": 94.10,
  "TotalVmsHosted": 45,
  "SuccessfulVmCompletions": 44
}
```

**Storage Overhead:** ~300 bytes (30 days × ~10 bytes per entry)

---

## 🔄 **How It Works in Real Time**

```
Timeline:

15:00:00 - Node sends heartbeat ✅
15:00:15 - Node sends heartbeat ✅
15:00:30 - Node sends heartbeat ✅
15:00:45 - Node CRASHES 💥

15:01:00 - Expected heartbeat NOT received ❌
15:01:05 - Node marked as Offline (20-second tolerance passed)

15:01:15 - Expected heartbeat NOT received ❌
15:01:30 - Expected heartbeat NOT received ❌
... (continues) ...

16:00:00 - Hourly maintenance runs 🔍
           └── Detects node offline
           └── Calculates: 59 minutes missed = 236 heartbeats
           └── Records: FailedHeartbeatsByDay["2026-01-30"] = 236
           └── Calculates uptime: 99.86% (assuming rest was online)

17:00:00 - Hourly maintenance runs again 🔍
           └── Still offline
           └── Adds 1 more hour = 240 heartbeats
           └── Updates: FailedHeartbeatsByDay["2026-01-30"] = 476
           └── Recalculates uptime: 99.73%

18:30:00 - Node comes back online! 🟢
           └── Sends heartbeat
           └── Status = Online

19:00:00 - Hourly maintenance runs 🔍
           └── Node is online now
           └── No new failures to record
           └── Uptime still shows 99.73% (history preserved!)
```

---

## 🎓 **Summary**

Your approach tracks uptime by counting failed heartbeats per day in a simple dictionary.

**Key Points:**
1. **Period**: Rolling 30-day window (or since registration if newer)
2. **Detection**: Hourly maintenance checks offline nodes
3. **Recording**: Distributes missed heartbeats across days
4. **Protection**: Timestamp prevents double-counting
5. **Calculation**: `(Total Expected - Total Failed) / Total Expected × 100`
6. **Cleanup**: Auto-removes data older than 30 days

**Result:** Simple, accurate, historically-aware uptime tracking! 🚀

---

## 🧪 **Testing the System**

1. **Register a node** - Should show 100% uptime
2. **Stop the node** - After 20 seconds, marked offline
3. **Wait 1+ hours** - Hourly maintenance records failures
4. **Check uptime** - Should decrease based on downtime
5. **Restart node** - Uptime percentage persists (not reset!)
6. **Wait 30 days** - Old failure data auto-removed

Your node marketplace will now show **real, historical uptime data**! 🎉
