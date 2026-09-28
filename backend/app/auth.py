import hmac

from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from app.config import get_settings

bearer = HTTPBearer(auto_error=False)


def require_token(creds: HTTPAuthorizationCredentials | None = Depends(bearer)) -> None:
    # ponytail: single shared secret; add users + JWT roles when there's more than one operator
    if not creds or not hmac.compare_digest(creds.credentials.encode(), get_settings().api_secret.encode()):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Invalid or missing token",
                            headers={"WWW-Authenticate": "Bearer"})
