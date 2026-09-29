-- Run this against your MemoryOpsAI database first.
CREATE DATABASE MemoryOpsAI;
GO

USE MemoryOpsAI;
GO

CREATE TABLE Incidents
(
    IncidentId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerName VARCHAR(200),
    ModuleName VARCHAR(100),
    IssueDescription VARCHAR(MAX),
    ErrorDetails VARCHAR(MAX),
    Resolution VARCHAR(MAX),
    ResolutionStatus VARCHAR(50),
    CreatedDate DATETIME DEFAULT GETDATE()
);
GO

-- Handy later, once Phase 3 (agent recommendation) is wired in:
-- UPDATE Incidents SET Resolution = @Resolution, ResolutionStatus = 'Resolved' WHERE IncidentId = @IncidentId;
