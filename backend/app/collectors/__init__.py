from app.collectors.base import Collector
from app.collectors.fmcsa import FmcsaCollector

COLLECTORS: dict[str, type[Collector]] = {FmcsaCollector.name: FmcsaCollector}
