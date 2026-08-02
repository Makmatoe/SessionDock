namespace HandleScope.Models;

public sealed record ProcessSnapshot(
    ProcessRow Row,
    ProcessIdentity Identity);
