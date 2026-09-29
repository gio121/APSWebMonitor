import sqlite3

conn = sqlite3.connect('/mnt/c/REPO/Monitores/Antigravity/ApsMonitor/aps.db')
cur = conn.cursor()

rows = cur.execute("SELECT * FROM Commands").fetchall()
cols = [c[1] for c in cur.execute("PRAGMA table_info(Commands)").fetchall()]
print(f"Total commands: {len(rows)}")
for r in rows:
    print(dict(zip(cols, r)))
