# PRD: Agent Live Widget
### ווידג'ט תמידי-על-המסך למעקב חי אחר Claude Code

**גרסה:** 1.0
**תאריך:** יולי 2026
**סטטוס:** מוכן ל-Claude Code

---

## 1. סקירה כללית (Overview)

### 1.1 הבעיה
כשעובדים עם Claude Code על פרויקט (במיוחד ב-VS Code), אין נראות חיה למה שהסוכן עושה ברגע נתון: אילו כלים הוא מפעיל, על אילו קבצים הוא עובד, כמה "לחץ זיכרון" (context) נצבר, ומתי הוא מתקרב ל-compaction. כרגע צריך לפתוח את הטרמינל/פאנל של Claude Code כדי לראות משהו, וגם אז זה טקסטואלי ולא תמציתי.

### 1.2 הפתרון
ווידג'ט Desktop קטן, **תמיד-מעל-כל-החלונות** (always-on-top), שיושב בפינת המסך בזמן שעובדים ב-VS Code, ומציג בזמן אמת:
- מה הסוכן עושה כרגע (idle / חושב / מריץ כלי / ממתין לאישור)
- באיזה כלי משתמש כרגע (Write / Edit / Bash / Read וכו') ועל איזה קובץ
- Prompt אחרון שנשלח
- מונה session פעיל וזמן ריצה
- אינדיקציית "לחץ זיכרון" (מתקרב ל-compaction)
- לוג קצר של האירועים האחרונים

### 1.3 מחוץ לתחום (Non-Goals)
- אין צורך בשליטה דו-כיוונית (הווידג'ט לא שולח פקודות ל-Claude Code, רק צופה)
- לא תומך במספר sessions מקבילים בגרסה הראשונה (V1 = session בודד לפי תיקיית פרויקט)
- אין תמיכה ב-macOS/Linux בגרסה הראשונה (Windows + WPF בלבד)

---

## 2. ארכיטקטורה כללית

```
┌─────────────────────┐     stdin JSON      ┌──────────────────────┐
│   Claude Code CLI    │ ───────────────────▶│   Hook Scripts        │
│   (רץ בתוך VS Code)  │   (per event)        │   (Node.js)           │
└─────────────────────┘                       └──────────┬───────────┘
                                                           │ HTTP POST
                                                           ▼
                                               ┌──────────────────────┐
                                               │  Local State Server   │
                                               │  (Node/Express)       │
                                               │  localhost:4577       │
                                               └──────────┬───────────┘
                                                           │ WebSocket
                                                           ▼
                                               ┌──────────────────────┐
                                               │   WPF Widget          │
                                               │   Always-On-Top       │
                                               └──────────────────────┘
```

שלושה רכיבים נפרדים, כל אחד responsibility יחיד:

1. **Hook Scripts** — נתקעים בתוך lifecycle של Claude Code, כותבים אירועים
2. **Local State Server** — תהליך רקע יחיד שמאגד את כל האירועים ל-state קוהרנטי ומשדר אותו
3. **WPF Widget** — צרכן טהור של ה-state, מציג ויזואלית בלבד

הפרדה כזו מאפשרת להריץ את השרת פעם אחת ולחבר אליו כמה widgets (או אפילו דשבורד web) בעתיד, בלי לגעת בשכבת ה-hooks.

---

## 3. שכבה 1: Hook Scripts

### 3.1 אירועים לרישום
נרשמים בקובץ `.claude/settings.json` בפרויקט:

| אירוע | מטרה |
|---|---|
| `SessionStart` | איפוס state, שליחת session_id, cwd, זמן התחלה |
| `UserPromptSubmit` | עדכון "prompt אחרון", מעבר למצב "חושב" |
| `PreToolUse` | עדכון "כלי פעיל כרגע" + הפרמטרים (למשל שם קובץ) |
| `PostToolUse` | סימון הכלי כהושלם, הוספה ללוג אירועים |
| `Notification` | הצגת התראה בווידג'ט (למשל: "ממתין לאישור") |
| `PreCompact` | הדלקת דגל "לחץ זיכרון גבוה" בווידג'ט |
| `Stop` | מעבר למצב "idle" |
| `SessionEnd` | סגירת session, ניקוי state |

### 3.2 מבנה סקריפט ה-hook (Node.js)
כל hook הוא סקריפט קצר שקורא JSON מ-stdin ושולח POST לשרת המקומי. דוגמה עקרונית ל-`PostToolUse`:

```javascript
#!/usr/bin/env node
// hooks/post-tool-use.js
const http = require('http');

let input = '';
process.stdin.on('data', chunk => input += chunk);
process.stdin.on('end', () => {
  const event = JSON.parse(input);
  const payload = JSON.stringify({
    type: 'PostToolUse',
    sessionId: event.session_id,
    toolName: event.tool_name,
    toolInput: event.tool_input,
    filePath: event.tool_input?.file_path,
    timestamp: Date.now()
  });

  const req = http.request({
    hostname: 'localhost',
    port: 4577,
    path: '/event',
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    timeout: 500 // לעולם לא לחסום את Claude Code אם השרת לא רץ
  });
  req.on('error', () => {}); // כשל שקט - הווידג'ט לא קריטי לעבודה
  req.write(payload);
  req.end();
  process.exit(0); // תמיד exit 0 - זה hook תצפיתי בלבד, לא חוסם
});
```

**עיקרון קריטי:** אף hook לא מחזיר קוד יציאה חוסם (2) ואף לא מחכה לתגובה מהשרת. אם הווידג'ט לא רץ, Claude Code ממשיך לעבוד כרגיל ללא שום עיכוב. זהו hook תצפיתי (observability) בלבד — לא בקרה.

### 3.3 קונפיגורציית `settings.json`
```json
{
  "hooks": {
    "SessionStart": [{ "hooks": [{ "type": "command", "command": "node .claude/hooks/session-start.js" }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "node .claude/hooks/prompt-submit.js" }] }],
    "PreToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "node .claude/hooks/pre-tool-use.js", "async": true }] }],
    "PostToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "node .claude/hooks/post-tool-use.js", "async": true }] }],
    "PreCompact": [{ "hooks": [{ "type": "command", "command": "node .claude/hooks/pre-compact.js" }] }],
    "Stop": [{ "hooks": [{ "type": "command", "command": "node .claude/hooks/stop.js" }] }]
  }
}
```
שימוש ב-`async: true` ל-Pre/PostToolUse כדי שהשליחה תרוץ ברקע בלי להאט אף כלי.

---

## 4. שכבה 2: Local State Server

### 4.1 אחריות
- מאזין ל-POST על `/event` מה-hooks
- שומר state נוכחי בזיכרון (לא צריך DB — תהליך קליל ל-session אחד)
- משדר עדכונים לכל לקוח מחובר דרך WebSocket
- חושף גם endpoint `GET /state` לקריאה חד-פעמית (למקרה שהווידג'ט מתחבר מאוחר)

### 4.2 מבנה ה-State
```typescript
interface AgentState {
  sessionId: string | null;
  status: 'idle' | 'thinking' | 'running_tool' | 'waiting_approval' | 'offline';
  currentTool: { name: string; filePath?: string; startedAt: number } | null;
  lastPrompt: string | null;
  sessionStartedAt: number | null;
  memoryPressure: 'normal' | 'high'; // מוגדל ע"י PreCompact
  recentEvents: Array<{
    type: string;
    label: string;      // תיאור קצר לתצוגה
    timestamp: number;
  }>; // עד 20 אחרונים
  projectPath: string | null;
}
```

### 4.3 API
| Method | Path | תיאור |
|---|---|---|
| POST | `/event` | קליטת אירוע מ-hook |
| GET | `/state` | State נוכחי (JSON) |
| WS | `/live` | שידור עדכוני state בזמן אמת |

### 4.4 הרצה
תהליך רקע קל (Node.js, ~50 שורות) שמופעל אוטומטית עם `SessionStart` (אם לא רץ כבר) או נרשם כ-Windows service/Tray app נפרד. מומלץ V1: הרצה ידנית עם `npm run widget-server`, ובעתיד — הפעלה אוטומטית מתוך ה-widget עצמו (spawns את השרת אם הוא לא זמין ב-localhost:4577).

---

## 5. שכבה 3: WPF Widget

### 5.1 עקרונות עיצוב
- **חלון שקוף, ללא מסגרת (borderless), Always-On-Top** — `Topmost="True"`, `WindowStyle="None"`, `AllowsTransparency="True"`
- **גודל קומפקטי** — כ-260×160px, פינה עליונה-ימנית של המסך (ניתן לגרירה)
- **מצב "מכווץ"** — אייקון קטן בלבד עם נקודת סטטוס צבעונית (ירוק=idle, כחול פועם=running, כתום=waiting, אדום=offline), מתרחב בלחיצה/hover
- **עיצוב חשוך** — תואם את הפלטת dark-mode שאתה נוהג להשתמש בה בפרויקטים שלך

### 5.2 מסכים/פאנלים
1. **Header** — שם הפרויקט (מ-`projectPath`), נקודת סטטוס, זמן session
2. **Current Activity** — כלי פעיל + קובץ (עם אייקון לפי סוג כלי), עם spinner קטן כשרץ
3. **Last Prompt** — שורה אחת מקוצרת (עד ~60 תווים) של ה-prompt האחרון
4. **Memory Pressure Bar** — פס דק שמשנה צבע (ירוק→כתום→אדום) לפי `memoryPressure`, עם pulse כש-high
5. **Event Log (מורחב)** — רשימה גוללת של 20 האירועים האחרונים, כל אחד עם timestamp יחסי ("לפני 3 שניות")

### 5.3 חיבור לשרת
- `ClientWebSocket` ל-`ws://localhost:4577/live`
- Reconnect אוטומטי כל 3 שניות אם החיבור נופל
- אם לא מצליח להתחבר תוך 10 שניות — הצגת מצב "offline" (נקודה אדומה)

### 5.4 טכנולוגיות
- .NET 8 / WPF
- `System.Net.WebSockets.Client` לחיבור WS
- `System.Text.Json` לפרסור state
- אפשר `Hardcodet.NotifyIcon.Wpf` להצגת אייקון ב-system tray עם תפריט (Show/Hide/Exit)

---

## 6. מבנה קבצים מוצע

```
agent-live-widget/
├── hooks/
│   ├── session-start.js
│   ├── prompt-submit.js
│   ├── pre-tool-use.js
│   ├── post-tool-use.js
│   ├── pre-compact.js
│   └── stop.js
├── server/
│   ├── index.js          # Express + ws server
│   ├── state.js          # ניהול ה-state בזיכרון
│   └── package.json
├── widget/
│   ├── AgentWidget.sln
│   ├── MainWindow.xaml
│   ├── MainWindow.xaml.cs
│   ├── WebSocketClient.cs
│   ├── AgentState.cs      # מודל תואם ל-TypeScript interface
│   └── Assets/
├── .claude/
│   └── settings.json       # רישום ה-hooks (לפרויקט שרוצים לעקוב אחריו)
└── README.md
```

---

## 7. שלבי מימוש מוצעים

| שלב | תוצר | הערכה |
|---|---|---|
| 1 | Local State Server + hooks בסיסיים (SessionStart, PreToolUse, PostToolUse, Stop) | ליבה עובדת, ניתן לבדוק עם `curl`/Postman |
| 2 | WPF Widget מינימלי — חיבור WS + הצגת status דוט בלבד | אימות end-to-end |
| 3 | UI מלא — כל הפאנלים מסעיף 5.2, אנימציות, מצב מכווץ/מורחב | חוויית משתמש סופית |
| 4 | Notification, PreCompact, SessionEnd + auto-reconnect ו-error states | חוסן ל-production |
| 5 | Tray icon, הפעלה אוטומטית של השרת מתוך הווידג'ט, הגדרות (מיקום, שקיפות) | polish |

---

## 8. שיקולי קצה (Edge Cases)

- **כמה sessions/פרויקטים בו-זמנית** — V1 תומך בפרויקט אחד; השרת מבחין לפי `session_id` אבל מציג רק את האחרון הפעיל. הרחבה עתידית: טאבים בווידג'ט לפי `projectPath`.
- **Claude Code נסגר בלי `SessionEnd`** — Watchdog בשרת: אם לא הגיע אירוע 5+ דקות, לעבור אוטומטית למצב `idle`/`offline`.
- **השרת קורס** — הווידג'ט חייב לזהות ניתוק ולא "לתקוע" על state ישן; להציג "offline" מיד.
- **פרטיות** — `tool_input` עלול להכיל תוכן קבצים רגיש (למשל תוכן Write מלא). מומלץ לשלוח מהHook רק שדות מינימליים (שם קובץ, סוג כלי) ולא לשדר content מלא לשרת/לווידג'ט.

---

## 9. הרחבות עתידיות (מחוץ ל-V1)

- תמיכה במספר sessions/פרויקטים בו-זמנית עם טאבים
- גרסת macOS (Electron או Avalonia במקום WPF)
- אינטגרציה עם SYNAPSE — הצגת כמה "סוכנים" יחד אם בעתיד ירוץ orchestration מרובה-סוכנים על גבי Claude Agent SDK
- ייצוא לוג session ל-JSON/CSV לניתוח לאחור
- מצב "פוקוס" — הבהוב/צליל כשה-status עובר ל-`waiting_approval`

---

## 10. קריטריוני קבלה (Definition of Done ל-V1)

- [ ] הווידג'ט צף מעל VS Code וכל חלון אחר, בפינת מסך קבועה, נגרר בעכבר
- [ ] בזמן שClaude Code מריץ כלי — הווידג'ט מציג את שם הכלי והקובץ תוך פחות משנייה מרגע ה-PreToolUse
- [ ] לוג האירועים מציג לפחות 20 אירועים אחרונים עם timestamp יחסי
- [ ] ניתוק/חיבור מחדש של השרת לא גורם לקריסת הווידג'ט
- [ ] אף hook לא מוסיף יותר מ-50ms להשהיית כלי (async + non-blocking exit)
- [ ] PreCompact מדליק אינדיקציה ויזואלית תוך שנייה אחת
