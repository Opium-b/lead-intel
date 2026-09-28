import logging

from fastapi import APIRouter, Depends, FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.api import admin, leads, stats
from app.auth import require_token
from app.config import get_settings

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
logging.getLogger("httpx").setLevel(logging.WARNING)

app = FastAPI(title="Lead Intelligence API", version="0.1.0")
app.add_middleware(CORSMiddleware, allow_origins=[get_settings().frontend_origin],
                   allow_methods=["GET", "POST", "PUT", "PATCH"], allow_headers=["Authorization", "Content-Type"])

api = APIRouter(prefix="/api")


@api.get("/health", tags=["meta"])
def health():
    return {"ok": True}


protected = APIRouter(dependencies=[Depends(require_token)])
for router in (leads.router, leads.catalog_router, stats.router, admin.router):
    protected.include_router(router)
api.include_router(protected)
app.include_router(api)
