import os

from fastapi.testclient import TestClient

from app.main import app
from app.pipeline import ingest, process_companies
from tests.test_core import TODAY, batch

client = TestClient(app)
AUTH = {"Authorization": f"Bearer {os.environ['API_SECRET']}"}


def test_auth_required(db):
    assert client.get("/api/health").status_code == 200
    assert client.get("/api/leads").status_code == 401
    assert client.get("/api/leads", headers={"Authorization": "Bearer wrong"}).status_code == 401


def test_lead_flow(db):
    process_companies(db, ingest(db, batch()), TODAY)
    page = client.get("/api/leads", headers=AUTH, params={"signal_type": "CRASH", "state": "tx"}).json()
    assert page["total"] == 1 and page["items"][0]["phone"] == "2145550100"
    lead_id = page["items"][0]["id"]

    detail = client.get(f"/api/leads/{lead_id}", headers=AUTH).json()
    assert detail["signals"][0]["severity"] == "critical"  # sorted most severe first
    assert detail["score"] == min(100, sum(b["points"] for b in detail["score_breakdown"]))

    assert client.patch(f"/api/leads/{lead_id}", headers=AUTH, json={"status": "BOGUS"}).status_code == 422
    assert client.patch(f"/api/leads/{lead_id}", headers=AUTH,
                        json={"status": "CONTACTED", "channel": "pigeon"}).status_code == 422
    r = client.patch(f"/api/leads/{lead_id}", headers=AUTH,
                     json={"status": "NO_ANSWER", "channel": "phone", "note": " left voicemail "}).json()
    change = next(e for e in r["events"] if e["event_type"] == "STATUS_CHANGED")
    assert change["meta"] == {"from": "NEW", "to": "NO_ANSWER", "channel": "phone", "note": "left voicemail"}
    r = client.patch(f"/api/leads/{lead_id}", headers=AUTH, json={"status": "REVIEWED"}).json()
    assert r["status"] == "REVIEWED" and r["events"][0]["event_type"] == "STATUS_CHANGED"
    assert client.post(f"/api/leads/{lead_id}/notes", headers=AUTH, json={"text": "called"}).status_code == 201

    assert detail["pitch"][0]["services"] and detail["pitch"][0]["reasons"]
    assert client.get("/api/leads", headers=AUTH, params={"service_line": "eld_hos"}).json()["total"] == 1
    assert client.get("/api/leads", headers=AUTH, params={"service_line": "back_office"}).json()["total"] == 1
    assert client.get("/api/leads", headers=AUTH, params={"service_line": "no_such_line"}).json()["total"] == 0
    assert len(client.get("/api/service-lines", headers=AUTH).json()) == 9

    rules = client.get("/api/scoring-rules", headers=AUTH).json()
    assert client.put(f"/api/scoring-rules/{rules[0]['key']}", headers=AUTH, json={"weight": 500}).status_code == 422
    assert client.get("/api/stats/overview", headers=AUTH).json()["leads"] == 1
