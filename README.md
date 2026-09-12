# Agent Live Widget

ווידג'ט Desktop קטן, **תמיד-מעל-כל-החלונות**, שמציג בזמן אמת מה Claude Code עושה:
סטטוס הסוכן, הכלי הפעיל והקובץ, ה-prompt האחרון, לחץ context, ולוג אירועים חי.
תומך במעקב אחרי **כמה פרויקטים במקביל** — כל אחד מקבל טאב משלו.

![Architecture](PRD-Agent-Live-Widget.md)

```
Claude Code (hooks) ──HTTP──▶ State Server (localhost:4577) ──WebSocket──▶ WPF Widget
```

## רכיבים

| תיקייה | תפקיד |
|---|---|
| `hooks/` | סקריפט hook אוניברסלי (`send-event.js`) שמדווח אירועי lifecycle לשרת |
| `server/` | שרת state מקומי (Node.js) — `POST /event`, `GET /state`, `WS /live`. עוקב אחרי עד 6 פרויקטים במקביל (לפי `cwd`), כל אחד עם ה-state המלא שלו |
| `widget/` | הווידג'ט עצמו (.NET 9 / WPF) — always-on-top, dark, אנימציות חיות, טאבים לפרויקטים, הפעלה עצמית של השרת |

## התקנה והרצה

### הפעלה מהירה

```powershell
cd server && npm install     # פעם אחת בלבד
cd ..\widget && dotnet build -c Release
.\bin\Release\net10.0-windows\AgentLiveWidget.exe
```

**אין צורך להריץ את השרת בנפרד** — הווידג'ט בודק אם הוא כבר רץ על `localhost:4577`, ואם לא, מריץ אותו בעצמו (`ServerLauncher.cs`). אם השרת קורס תוך כדי ריצה הווידג'ט גם ינסה להפעיל אותו מחדש אוטומטית.

לחלופין — `.\start-widget.cmd` מריץ את שניהם במפורש.

### חיבור פרויקט למעקב

**כדי שכל פרויקט חדש ייכלל אוטומטית, בלי הרצה ידנית** — מריצים את הסקריפט פעם אחת בלבד
מול תיקיית הבית (`%USERPROFILE%`), כדי שהוא ימזג את ה-hooks לתוך ה-**global settings** של
Claude Code (`~/.claude/settings.json`) במקום settings.json של פרויקט בודד:

```powershell
node "C:/AllMyProjetcs/AgentLiveWidget/hooks/attach-project.js" "$env:USERPROFILE"
```

global settings חלים על **כל** session שנפתח, בכל פרויקט — קיים או עתידי — בלי צורך לחזור
ולהריץ את הסקריפט שוב לכל פרויקט חדש. זה כבר בוצע פעם אחת במחשב הזה.

לחלופין, אפשר עדיין לחבר **פרויקט בודד** בלבד (למשל אם רוצים לעקוב רק אחרי חלק מהפרויקטים):

```powershell
node "C:/AllMyProjetcs/AgentLiveWidget/hooks/attach-project.js" "<נתיב-לפרויקט>"
```

הסקריפט יוצר את `.claude/settings.json` אם הוא לא קיים, וממזג לתוכו את שמונת ה-hooks של הווידג'ט
בלי לגעת ב-hooks אחרים שכבר רשומים שם (למשל graphify ב-PensiaMng — נבדק ומאומת שהוא לא נוגע בהם).
בטוח להריץ כמה פעמים — לא יוצר כפילויות (מדלג על hooks שכבר קיימים).

**אם לפרויקט כבר היה `.claude/settings.json`** (אפילו רק עם permissions, כמו PensiaMng) — אין
צורך להפעיל מחדש session. Claude Code מנטר עריכות לקובץ קיים וטוען אותן אוטומטית.

**אם זו היצירה הראשונה של `.claude/settings.json` בפרויקט הזה** (לא היה שם קודם, אולי היה רק
`settings.local.json`) — וכבר יש session פתוח שם, **יש להפעיל אותו מחדש**. התיעוד הרשמי מבטיח
hot-reload רק ל*עריכות* בקובץ קיים, לא ליצירה של קובץ חדש תוך כדי ריצה — נבדק בפועל ואומת
שזה המקרה שגורם ל-hooks לא לפעול. הסקריפט `attach-project.js` מדפיס אזהרה מתאימה כשזה קורה.

השרת עוקב אחרי עד 6 פרויקטים במקביל (לפי `cwd` מנורמל); כשיש יותר, הפרויקט הכי פחות פעיל מוסר מהזיכרון.

## מה רואים בווידג'ט

- **נקודת סטטוס** עם glow פועם — ירוק=idle, סגול=חושב, טורקיז=מריץ כלי, ענבר=ממתין לאישור, אדום=offline
- **טאבים לפרויקטים** — מופיעים אוטומטית רק כשעוקבים אחרי יותר מפרויקט אחד; כל טאב עם נקודת סטטוס צבעונית משלו ושם הפרויקט. לחיצה על טאב "מצמידה" אותו (הבחירה שלך נשארת גם אם פרויקט אחר הופך פעיל יותר), עד שתבחר טאב אחר
- **פעילות נוכחית** — שם הכלי + הקובץ, עם spinner וזמן ריצה חי
- **Prompt אחרון** — שורה מקוצרת (tooltip מציג את המלא)
- **Context pressure** — פס שמתמלא ופועם באדום כש-compaction מתקרב (PreCompact)
- **Recent activity** — 20 האירועים האחרונים עם זמן יחסי
- **מצב מכווץ** — pill קטן עם סטטוס בלבד (כפתור ה-chevron בכותרת); מציג רק את הפרויקט הנבחר, בלי טאבים
- **System tray** — צבע האייקון משקף את הסטטוס של הפרויקט הנבחר; לחיצה כפולה מציגה/מסתירה, תפריט ימני ליציאה

## עקרונות בטיחות

- ה-hooks הם **תצפיתיים בלבד**: timeout של 400ms, כשל שקט, `exit 0` תמיד — Claude Code לעולם לא נחסם, גם אם השרת לא רץ.
- **פרטיות**: נשלחים רק שדות מינימליים (שם כלי, נתיב קובץ, prompt מקוצר) — לעולם לא תוכן קבצים או פקודות מלאות. הכול נשאר על `127.0.0.1`.
- **Watchdog**: אם פרויקט מסוים לא שלח אירוע 5 דקות, הוא (ורק הוא) חוזר אוטומטית ל-idle — שאר הטאבים לא מושפעים.

## בדיקה ידנית (ללא Claude Code)

```powershell
# פרויקט אחד — ללא טאבים:
Invoke-RestMethod -Uri http://localhost:4577/event -Method Post -ContentType 'application/json' -Body '{"type":"SessionStart","sessionId":"test","cwd":"e:/AllMyProjects/AgentLiveWidget"}'
Invoke-RestMethod -Uri http://localhost:4577/event -Method Post -ContentType 'application/json' -Body '{"type":"PreToolUse","toolName":"Edit","filePath":"widget/MainWindow.xaml"}'

# פרויקט שני — מדליק את שורת הטאבים בווידג'ט:
Invoke-RestMethod -Uri http://localhost:4577/event -Method Post -ContentType 'application/json' -Body '{"type":"SessionStart","sessionId":"test2","cwd":"e:/AllMyProjects/PensiaMng"}'

Invoke-RestMethod -Uri http://localhost:4577/state | ConvertTo-Json -Depth 5
```

`GET /state` מחזיר `{ projects: [...], activeProjectKey }` — מערך של כל הפרויקטים הנעקבים (עד 6) ומפתח הפרויקט הפעיל האחרון.
