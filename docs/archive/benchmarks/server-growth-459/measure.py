"""Synthetic SQLite sizing, not a production/API latency benchmark. Run from repository root."""
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import tempfile
import time

source = Path('src/CodexServer/RegistryPersistence.cs').read_text()
oauth = Path('src/CodexServer/McpOAuthStore.cs').read_text()
with tempfile.TemporaryDirectory() as temporary:
    db = sqlite3.connect(Path(temporary) / 'sizing.db')
    for table in ('workers', 'projects'):
        db.execute(re.search(r'CREATE TABLE IF NOT EXISTS ' + table + r' \(.*?\);', source, re.S)[0])
    for statement in re.findall(r'CREATE (?:TABLE|INDEX) IF NOT EXISTS .*?;', oauth, re.S):
        db.execute(statement)
    for size in (10, 100, 1000):
        for i in range(db.execute('SELECT count(*) FROM workers').fetchone()[0], size):
            registration = json.dumps(dict(ContractVersion=2, WorkerId=f'{i:032x}', DisplayName=f'Worker {i:04}', WorkerVersion='0.15.0', Platform='linux', Capacity=2, Capabilities=[]))
            project = json.dumps(dict(id=f'{i:032x}', displayName=f'Project {i:04}', repository=f'team/repository-{i}', baseBranch='main', description='Synthetic inventory sizing', requirements=[], revision=1))
            db.execute('INSERT INTO workers(worker_id,display_name,registered_at_utc,registration_json) VALUES(?,?,?,?)', (f'{i:032x}', f'Worker {i:04}', '2026-01-01T00:00:00Z', registration))
            db.execute('INSERT INTO projects VALUES(?,?,?,?)', (f'{i:032x}', f'Project {i:04}', project, '2026-01-01T00:00:00Z'))
        db.commit()
        start = time.perf_counter()
        workers = db.execute('SELECT registration_json FROM workers ORDER BY worker_id').fetchall()
        projects = db.execute('SELECT configuration_json FROM projects ORDER BY lower(display_name),project_id').fetchall()
        print(json.dumps(dict(inventory=size, projectedReadMs=round((time.perf_counter()-start)*1000, 3), storedJsonBytes=sum(len(row[0].encode()) for row in workers+projects), databaseBytes=db.execute('PRAGMA page_count').fetchone()[0]*db.execute('PRAGMA page_size').fetchone()[0])))
    # One grant: used code, latest access and R+1 refresh digests after R rotations.
    db.execute('INSERT INTO mcp_oauth_grants VALUES(?,?,?)', ('grant', '{}', 7776000))
    for rotations in (100, 2160, 129600):
        db.execute('DELETE FROM mcp_oauth_credentials')
        db.executemany('INSERT INTO mcp_oauth_credentials VALUES(?,?,?,?,?)', ((hashlib.sha256(str(i).encode()).hexdigest().upper(), 'grant', 'refresh' if i > 1 else ('code' if i == 0 else 'access'), 7776000, int(i != 1 and i != rotations+2)) for i in range(rotations+3)))
        db.commit()
        start = time.perf_counter()
        assert db.execute('SELECT used FROM mcp_oauth_credentials WHERE hash=?', (hashlib.sha256(b'0').hexdigest().upper(),)).fetchone()[0] == 1
        lookup_ms = (time.perf_counter()-start)*1000
        print(json.dumps(dict(rotations=rotations, credentialRows=db.execute('SELECT count(*) FROM mcp_oauth_credentials').fetchone()[0], lookupMs=round(lookup_ms, 3), databaseBytes=db.execute('PRAGMA page_count').fetchone()[0]*db.execute('PRAGMA page_size').fetchone()[0])))
