from abc import ABC, abstractmethod
import asyncio

class ConversationStorage(ABC):
    @abstractmethod
    async def get_internal_id(self, external_id: str) -> str | None:
        """
        Retrieves the internal conversation ID for a given external ID.
        Returns None if no mapping is found.
        """
        pass

    @abstractmethod
    async def save_mapping(self, external_id: str, internal_id: str):
        """
        Saves a mapping between an external and internal conversation ID.
        """
        pass

class InMemoryStorage(ConversationStorage):
    """
    An in-memory implementation of ConversationStorage for development and testing.
    This storage is not shared between processes.
    """
    def __init__(self):
        self._data = {}
        self._lock = asyncio.Lock()
        print("--- CoreService: Initialized InMemoryStorage. ---")

    async def get_internal_id(self, external_id: str) -> str | None:
        async with self._lock:
            return self._data.get(external_id)

    async def save_mapping(self, external_id: str, internal_id: str):
        async with self._lock:
            self._data[external_id] = internal_id
            print(f"--- CoreService/InMemoryStorage: Saved mapping: {external_id} -> {internal_id} ---")
