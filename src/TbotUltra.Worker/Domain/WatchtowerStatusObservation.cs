namespace TbotUltra.Worker.Domain;

public sealed record WatchtowerStatusObservation(
    string AccountName,
    string VillageName,
    int? CoordX,
    int? CoordY,
    WatchtowerStatus Status);
