# RetroFS Management API

REST API for managing RetroFS servers remotely.

## Quick Start

### Running the API

```powershell
cd src\RetroIO.Management.Api
dotnet run
```

The API will start at:
- **Swagger UI**: http://localhost:5000
- **API Base**: http://localhost:5000/api/v1

### Testing with Swagger

1. Navigate to http://localhost:5000
2. Explore available endpoints
3. Click "Try it out" to test endpoints
4. View request/response examples

### Testing with curl

```bash
# Get server status
curl http://localhost:5000/api/v1/server/status

# Start server
curl -X POST http://localhost:5000/api/v1/server/start

# Get TNFS statistics
curl http://localhost:5000/api/v1/tnfs/statistics

# Stop server
curl -X POST http://localhost:5000/api/v1/server/stop
```

### Testing with PowerShell

```powershell
# Get server status
Invoke-RestMethod -Uri "http://localhost:5000/api/v1/server/status" -Method Get

# Start server
Invoke-RestMethod -Uri "http://localhost:5000/api/v1/server/start" -Method Post

# Get TNFS statistics
Invoke-RestMethod -Uri "http://localhost:5000/api/v1/tnfs/statistics" -Method Get
```

## Available Endpoints

### Server Management

- `GET /api/v1/server/status` - Get server status and uptime
- `POST /api/v1/server/start` - Start the RetroFS server
- `POST /api/v1/server/stop` - Stop the RetroFS server
- `POST /api/v1/server/restart` - Restart the RetroFS server

### TNFS Monitoring

- `GET /api/v1/tnfs/statistics` - Get TNFS server statistics
- `GET /api/v1/tnfs/sessions` - Get active TNFS sessions (TODO)

## Response Format

All endpoints return responses in this format:

```json
{
  "success": true,
  "data": { ... },
  "message": "Optional message",
  "error": null
}
```

Error responses:

```json
{
  "success": false,
  "data": null,
  "message": null,
  "error": {
    "code": "ERROR_CODE",
    "message": "Error description",
    "details": { }
  }
}
```

## Example Responses

### Server Status
```json
{
  "success": true,
  "data": {
    "isRunning": true,
    "uptime": "2h 15m 33s",
    "version": "1.0.0",
    "startedAt": "2025-10-05T10:30:00Z"
  }
}
```

### TNFS Statistics
```json
{
  "success": true,
  "data": {
    "activeConnections": 5,
    "totalRequests": 12543,
    "bytesTransferred": 1073741824,
    "filesTransferred": 245,
    "uptime": "2h 15m",
    "requestsPerSecond": 42.3,
    "mbPerSecond": 2.1
  }
}
```

## Development

### Building

```powershell
dotnet build
```

### Running in Development Mode

```powershell
dotnet run --environment Development
```

Development mode includes:
- Swagger UI at root (/)
- Detailed error messages
- CORS enabled for all origins

### Running in Production

```powershell
dotnet run --environment Production
```

Production mode:
- Swagger UI disabled
- Generic error messages
- CORS configured for specific origins

## Next Steps

### TODO: Additional Endpoints
- [ ] User management (CRUD)
- [ ] Group management (CRUD)
- [ ] Firewall rules management
- [ ] Volume operations
- [ ] Log streaming
- [ ] Settings management

### TODO: Security
- [ ] Authentication middleware
- [ ] JWT token support
- [ ] Rate limiting
- [ ] API key management

### TODO: Features
- [ ] WebSocket for real-time updates
- [ ] Health check endpoint
- [ ] Metrics endpoint (Prometheus format)
- [ ] API versioning

## Architecture

```
Client (RetroServeUI/CLI/Web)
    ↓ HTTP/HTTPS
Controllers (ServerController, TnfsController, etc.)
    ↓
Services (IServerManager, etc.)
    ↓
RetroFS Backend (SessionManager, IRetroIDP, etc.)
```

## Integration with RetroServeUI

Update RetroServeUI to use HTTP client instead of direct service access:

```csharp
// Before
var idp = _serverHost.GetRetroIDP();
var users = await idp.ListUsersAsync();

// After
var client = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
var response = await client.GetAsync("/api/v1/users");
var result = await response.Content.ReadFromJsonAsync<ApiResponse<UserListResponse>>();
```

## Full Path

This file is `src/RetroIO.Management.Api/src/README.md`.
