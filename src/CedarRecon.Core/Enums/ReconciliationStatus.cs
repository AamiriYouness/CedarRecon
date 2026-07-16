namespace CedarRecon.Core.Enums;

public enum ReconciliationStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    PartiallyCompleted  // some files succeeded, some dead-lettered
}
