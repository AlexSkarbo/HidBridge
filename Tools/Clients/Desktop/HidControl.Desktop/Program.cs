using HidControl.ClientSdk;

Console.WriteLine("HidControl.Desktop skeleton (Avalonia pending).");

// Touch the SDK so the project reference is exercised.
_ = new HidControlClient(new HttpClient(), new Uri("http://127.0.0.1:8080"));

