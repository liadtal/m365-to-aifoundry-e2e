"""
ProxyAgent CoreService - A FastAPI service that proxies requests to Azure AI Foundry agents.
Uses Microsoft Agent Framework for simplified agent interactions.
"""

import json
import os
import logging
import time
import uuid
from typing import Annotated, Any, AsyncGenerator, Dict, List
from enum import Enum
from contextlib import asynccontextmanager

from dotenv import load_dotenv
import uvicorn
from fastapi import FastAPI, Request, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel

from agent_framework.azure import AzureAIProjectAgentProvider
from agent_framework import ChatAgent, tool
from azure.identity.aio import DefaultAzureCredential

from storage import ConversationStorage, InMemoryStorage

# --- Configure logging ---
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

# --- Load environment variables ---
load_dotenv()

# --- Tool definitions (using @tool decorator) ---

# NOTE: approval_mode="never_require" is for sample brevity.
# Use "always_require" in production for tools that have side effects.
@tool(approval_mode="never_require")
def get_daily_tasks(
    username: Annotated[str, "The username of the person whose daily tasks are to be retrieved"]
) -> List[Dict[str, Any]]:
    """Get the daily tasks for today."""
    if "lital" in username.lower():
        return [
            {"id": "task1", "title": "Take daughter from kindergarten", "completed": False},
            {"id": "task2", "title": "Make dinner", "completed": True},
            {"id": "task3", "title": "Read a chapter in my book", "completed": False},
        ]
    else:
        return [
            {"id": "task1", "title": "Finish the quarterly report", "completed": False},
            {"id": "task2", "title": "Prepare for the team meeting", "completed": True},
            {"id": "task3", "title": "Review pull requests", "completed": False},
        ]

# --- Pydantic models for structured output ---

class EventStatus(str, Enum):
    """The status of the event."""
    NOT_COMPLETED = "NotCompleted"
    COMPLETED = "Completed"

class CalendarEvent(BaseModel):
    """Represents a calendar event."""
    model_config = {"extra": "forbid"}
    
    eventId: uuid.UUID
    """The unique identifier for the event."""
    eventTitle: str
    """The title of the event."""
    eventStatus: EventStatus

class Calendar(BaseModel):
    """Represents a list of calendar events with their details."""
    model_config = {"extra": "forbid"}
    
    events: List[CalendarEvent]

class Usage(BaseModel):
    """Represents token usage information."""
    input_tokens: int = 0
    output_tokens: int = 0
    total_tokens: int = 0

# --- AIF Client ---

class AIFClient:
    def __init__(self, chat_agent: ChatAgent, builder_agent: ChatAgent, storage: ConversationStorage):
        self.chat_agent = chat_agent
        self.builder_agent = builder_agent
        self.storage = storage

    async def generate_response(self, text: str, conversation_id: str) -> AsyncGenerator[str, None]:
        timings: Dict[str, Any] = {}
        total_start_time = time.time()
        
        try:
            # Determine which agent to use
            is_calendar_request = "calendar" in text.lower()
            agent = self.builder_agent if is_calendar_request else self.chat_agent
            logger.info(f"Using agent: {agent.name}")
            
            # Get or create thread for conversation
            thread_id = await self.storage.get_internal_id(conversation_id)
            if thread_id:
                logger.info(f"Found existing thread {thread_id} for conversation {conversation_id}")
                thread = agent.get_new_thread(service_thread_id=thread_id)
            else:
                logger.info(f"Creating new thread for conversation {conversation_id}")
                thread = agent.get_new_thread()
            
            timings["prep_time"] = time.time() - total_start_time
            
            # Add context for calendar requests
            user_input = text
            if is_calendar_request:
                user_input = f"{text}\n\nReturn the response in JSON format."

            # Stream the agent response
            generation_start = time.time()
            async for chunk in agent.run_stream(user_input, thread=thread):
                if chunk.text:
                    print(chunk.text, end="", flush=True)
                    yield format_sse(chunk.text)
                else:
                    for content in chunk.contents:
                        if content.type == "usage":
                            usage = Usage(
                                input_tokens=content.usage_details.get('input_token_count', 0) or 0,
                                output_tokens=content.usage_details.get('output_token_count', 0) or 0,
                                total_tokens=content.usage_details.get('total_token_count', 0) or 0,
                            )
                            yield format_sse(usage.model_dump_json(), event="usage")
                            logger.info(f"Sent usage event: {usage.model_dump()}")
            
            print()  # Newline after streaming
            
            # Save the thread mapping if this is a new conversation
            if not thread_id and thread.service_thread_id:
                await self.storage.save_mapping(conversation_id, thread.service_thread_id)
                logger.info(f"Saved thread mapping: {conversation_id} -> {thread.service_thread_id}")

        except Exception as e:
            logger.exception(f"Error during response generation: {e}")
            yield format_sse(json.dumps({"error": str(e)}), event="error")
        
        finally:
            timings["generation_time"] = time.time() - generation_start if 'generation_start' in locals() else 0
            timings["total_time"] = time.time() - total_start_time
            logger.info(f"Request timings: {json.dumps(format_timings(timings, timings['total_time']), indent=2)}")

# --- Application lifecycle ---

async def setup_agent(
    provider: AzureAIProjectAgentProvider,
    agent_name: str,
    tools: List[Any],
    response_format: Dict[str, Any] | None = None,
) -> ChatAgent:
    # Build default_options dict
    default_options: Dict[str, Any] | None = None
    if response_format:
        default_options = {"response_format": response_format}
    
    try:
        # Get the existing agent
        agent = await provider.get_agent(
            name=agent_name,
            tools=tools,
            default_options=default_options,
        )
        logger.info(f"Found existing agent: {agent_name} (ID: {agent.id})")
        
        # Check if the agent configuration matches what we want
        if agent.default_options.get("tools") == tools and agent.default_options.get("response_format") == response_format:
            logger.info(f"Agent '{agent_name}' configuration is up to date")
            return agent
        
        # Configuration differs - create a new version with updated definition
        logger.info(f"Agent '{agent_name}' configuration differs, creating new version")
        agent = await provider.create_agent(
            name=agent_name,
            model=agent.additional_properties.get("model_id"),
            instructions=agent.default_options.get("instructions", ""),
            tools=tools,
            default_options=default_options,
        )
        logger.info(f"Updated agent: {agent_name} (new ID: {agent.id})")
        return agent
        
    except Exception as e:
        raise ValueError(f"Error occurred while setup of agent '{agent_name}': {e}")

async def startup(app: FastAPI) -> None:
    logger.info("Starting up and initializing clients...")
    
    # Get configuration from environment variables
    project_endpoint = os.getenv("AIServices:AzureAIFoundryProjectEndpoint")
    if not project_endpoint:
        raise ValueError("AIServices:AzureAIFoundryProjectEndpoint environment variable is required")
    chat_agent_name = os.getenv("AIServices:ChatAgentID")
    if not chat_agent_name:
        raise ValueError("AIServices:ChatAgentID environment variable is required")
    builder_agent_name = os.getenv("AIServices:BuilderAgentID")
    if not builder_agent_name:
        raise ValueError("AIServices:BuilderAgentID environment variable is required")
    
    # Initialize Azure credentials and agent provider
    app.state.credential = DefaultAzureCredential()
    app.state.provider = AzureAIProjectAgentProvider(
        credential=app.state.credential,
        project_endpoint=project_endpoint,
    )
    
    # Define tools
    tools = [get_daily_tasks]
    
    # Setup chat agent
    chat_agent = await setup_agent(
        provider=app.state.provider,
        agent_name=chat_agent_name,
        tools=tools,
    )
    
    # Setup builder agent for structured JSON output (calendar)
    calendar_response_format = {
        "type": "json_schema",
        "json_schema": {
            "name": "calendar",
            "description": "Represents a list of calendar events with their details.",
            "strict": True,
            "schema": Calendar.model_json_schema()
        }
    }
    
    builder_agent = await setup_agent(
        provider=app.state.provider,
        agent_name=builder_agent_name,
        tools=tools,
        response_format=calendar_response_format,
    )
    
    # Initialize conversation storage
    storage = InMemoryStorage()
    
    # Initialize AIFClient
    app.state.aif_client = AIFClient(chat_agent, builder_agent, storage)
    
    logger.info("Startup complete.")


async def shutdown(app: FastAPI) -> None:
    logger.info("Shutting down and closing clients...")
    
    # Close provider (this also closes the internal project client)
    if hasattr(app.state, 'provider'):
        await app.state.provider.close()
    
    # Close credential
    if hasattr(app.state, 'credential'):
        await app.state.credential.close()
    
    logger.info("Shutdown complete.")

@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncGenerator[None, None]:
    await startup(app)
    yield
    await shutdown(app)

# --- FastAPI application ---

app = FastAPI(
    title="ProxyAgent CoreService",
    description="A service that proxies requests to Azure AI Foundry agents",
    lifespan=lifespan,
)

# --- API models ---

class StreamRequest(BaseModel):
    """Request model for the streaming endpoint."""
    text: str
    conversationId: str

# --- Helper functions ---

def format_sse(data: str, event: str = "text") -> str:
    """Format data as a Server-Sent Event."""
    lines = data.split('\n')
    sse_data = "\n".join(f"data: {line}" for line in lines)
    return f"event: {event}\n{sse_data}\n\n"

def format_timings(data: Any, total_time: float) -> Any:
    if isinstance(data, float):
        percentage = round(data * 100.0 / total_time, 2) if total_time > 0 else 0
        return f"{round(data, 2)}s ({percentage}%)"
    elif isinstance(data, dict):
        return {key: format_timings(value, total_time) for key, value in data.items()}
    elif isinstance(data, list):
        return [format_timings(item, total_time) for item in data]
    return data

# --- API endpoints ---
@app.post("/api/v1/messages")
async def stream_messages(payload: StreamRequest, request: Request):
    if not payload.conversationId:
        raise HTTPException(status_code=400, detail="conversationId cannot be null or empty.")
    
    aif_client: AIFClient = request.app.state.aif_client
    return StreamingResponse(
        aif_client.generate_response(payload.text, payload.conversationId),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache"}
    )


@app.get("/health")
async def health_check():
    """Health check endpoint."""
    return {"status": "healthy"}


# --- Main entry point ---

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
