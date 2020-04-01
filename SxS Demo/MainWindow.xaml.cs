using System;
using System.Windows;
using System.Windows.Controls;
using System.Text;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WebSocket4Net;

namespace SxS_Demo
{
    /// <summary>
    /// When this example application starts it attempts to create a Side by Side API session with Eikon API Proxy
    /// Once the session has been established the list of available color channels is retrieved from Eikon. This list populates the Combobox.
    /// The user can link application to Eikon on a color channel by selecting the channel from the Combobox.
    /// Once the application is linked to Eikon on a color channel context can be exchanged between application and Eikon.
    /// To send context to an Eikon app linked on the same color channel select "Send context to Eikon tab and click on an item in the Listbox.
    /// To view context received from Eikon click on "Context received from Eikon" tab.
    /// To send context from Eikon to the application change symbol in an Eikon app linked on the same color channel.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string appKeyError = "This example requires Eikon app key, which is missing in the source code. \n" +
            "To fix this open the source code, add the app key and recompile. \n" +
            "Click OK to close the application.";
        private const string eikonNotRunningError = "Eikon is not running. The application will terminate. \n" +
            "Click OK to close the application.";
        // The use of Eikon SxS API requires an app key and corresponding product ID.
        // Replace the app key and product ID in the source code in this example with the ones you generated using App Key Generator app in Eikon
        // For more details see documentation for Eikon Side by Side Integration API:
        // https://developers.refinitiv.com/eikon-apis/side-side-integration-api/quick-start
        //
        private readonly string appKey;
        private const string productID = "THEWOODBRIDGECOMPANY.SXSDEMOAPP";
        private const int defaultPortNumber = 9000;
        private const int maxPortsToTry = 5;
        private string sxsURL;
        private string sxsWebSocketURL;
        private static readonly HttpClient eikonSxSClient = new HttpClient();
        private WebSocket sxsWebSocketClient;
        private string httpResponse;
        private string sxsToken;

        public MainWindow()
        {
            InitializeComponent();

            lblColorChannel.Content = "Connecting to Eikon. Please wait...";
            /// Replace this with your Eikon app key generated using App Key Generator app in Eikon
            appKey = Environment.GetEnvironmentVariable("EIKON_APP_KEY");
            if (appKey == null)
            {
                MessageBox.Show(appKeyError, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
            async void EstablishSxSSession()
            {
                string sxsPingURL;
                string portNumber;
                // On startup Eikon API Proxy attempts to start listening on port 9000 if this port is available
                // If port 9000 is already occupied Eikon API Proxy starts listening on the next available port
                // Here we check if Eikon API Proxy is listening on port 9000. If not, we try port 9001 and so on until we exaust the port number range
                for (int i = 0; i <= maxPortsToTry; i++)
                {
                    portNumber = (defaultPortNumber + i).ToString();
                    sxsPingURL = "http://localhost:" + portNumber + "/ping";
                    Dispatcher.Invoke(() => { tbkStatus.Text = "Connecting to Eikon on port " + portNumber; });
                    try
                    {
                        System.Diagnostics.Debug.Print("Trying port number " + portNumber);
                        HttpResponseMessage response = await eikonSxSClient.GetAsync(sxsPingURL);
                        response.EnsureSuccessStatusCode();
                        httpResponse = response.Content.ReadAsStringAsync().Result;
                        if (portNumber == httpResponse)
                        {
                            sxsURL = "http://localhost:" + portNumber + "/sxs/v1/";
                            sxsWebSocketURL = "ws://localhost:" + portNumber + "/sxs/v1/notifications?sessionToken=";
                            break;
                        }
                    }
                    catch(HttpRequestException)
                    {
                    }
                    if (i == maxPortsToTry)
                    {
                        MessageBox.Show(eikonNotRunningError, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Dispatcher.Invoke(() => { Close(); });
                    }
                }

                sxsToken = await SxSHandShake();
                if (sxsToken != null)
                {
                    sxsWebSocketClient = new WebSocket(sxsWebSocketURL + sxsToken + "&linkType=3");
                    sxsWebSocketClient.Opened += new EventHandler(SxSWebSocketClient_Opened);
                    sxsWebSocketClient.Closed += new EventHandler(SxSWebSocketClient_Closed);
                    sxsWebSocketClient.MessageReceived += new EventHandler<MessageReceivedEventArgs>(SxSWebSocketClient_MessageReceived);
                    sxsWebSocketClient.Open();
                }
            }
            Task.Run(() => EstablishSxSSession());
        }

        async Task<string> SxSHandShake()
        {
            JObject msg = new JObject(
                              new JProperty("command", "handshake"),
                              new JProperty("productId", productID),
                              new JProperty("apiKey", appKey));
            JObject response = await PostMessageToEikon(msg);
            Boolean sxsHandshakeSuccess = (Boolean)response.SelectToken("isSuccess");
            if (sxsHandshakeSuccess)
            {
                Dispatcher.Invoke(() => { tbkStatus.Text = "Established SxS session with Eikon"; });
                return (string)response.SelectToken("sessionToken");
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    tbkStatus.Text = "Failed to establish HTTP session with Eikon\n" +
                        (string)response.SelectToken("error.message");
                });
                return null;
            }
        }

        private async void SxSWebSocketClient_Opened(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() => { tbkStatus.Text = "Eikon WebSocket opened"; });
            await GetColorChannels();
        }

        private void SxSWebSocketClient_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                tbkStatus.Text = "Received message from Eikon: " + e.Message;
                JObject json = JObject.Parse(e.Message);
                if ((string)json.SelectToken("command") == "contextReceived")
                {
                    string context = (string)json.SelectToken("context");
                    json = JObject.Parse(context);
                    string ric = (string)json.SelectToken("entities[0].RIC");
                    lblReceivedContext.Content = ric;
                }
            });
        }

        private void SxSWebSocketClient_Closed(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    tbkStatus.Text = "Eikon Websocket closed \n" + ((ClosedEventArgs)e).Reason;
                }
                catch (InvalidCastException)
                {
                }
                finally
                {
                    tabMain.IsEnabled = false;
                }
            });
        }

        async Task<JObject> PostMessageToEikon(JObject msg)
        {
            if (sxsToken != null) 
            {
                msg.Add("sessionToken", new JValue(sxsToken));
            }
            StringContent jsonMsg = new StringContent(msg.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await eikonSxSClient.PostAsync(sxsURL, jsonMsg);
            httpResponse = response.Content.ReadAsStringAsync().Result;
            return JObject.Parse(httpResponse);
        }

        private class ColorChannel
        {
            public ColorChannel(string color, string id)
            {
                Color = color;
                Id = id;
            }
            public string Color { get; set; }
            public string Id { get; set; }
        }

        async Task GetColorChannels()
        {
            JObject msg = new JObject(new JProperty("command", "getColorChannelList"));
            JObject response = await PostMessageToEikon(msg);
            Boolean isSuccess = (Boolean)response.SelectToken("isSuccess");
            if (isSuccess)
            {
                Dispatcher.Invoke(() => {
                    tbkStatus.Text = "Retrieved the list of color channels";
                    List<ColorChannel> colorChannels = new List<ColorChannel>();
                    foreach (JToken channel in response["channels"].Children())
                    {
                        colorChannels.Add(new ColorChannel((string)channel["color"], (string)channel["channelId"]));
                    }
                    cbxColorChannel.ItemsSource = colorChannels;
                    gbxSelectChannel.IsEnabled = true;
                    lblColorChannel.Content = "Select color channel to join:";
                });
            }
            else
            {
                Dispatcher.Invoke(() => {
                    tbkStatus.Text = "Failed to retrieve the list of color channels \n" +
                        (string)response.SelectToken("error.message");
                });
            }
        }

        private async void JoinColorChannel(object sender, SelectionChangedEventArgs e)
        {
            string channelId = (cbxColorChannel.SelectedItem as ColorChannel).Id;
            JObject msg = new JObject(
                              new JProperty("command", "joinColorChannel"),
                              new JProperty("channelId", channelId));
            JObject response = await PostMessageToEikon(msg);
            Boolean isSuccess = (Boolean)response.SelectToken("isSuccess");
            if (isSuccess)
            {
                Dispatcher.Invoke(() => {
                    tbkStatus.Text = "Joined color channel with channelId " + channelId;
                    tabMain.IsEnabled = true;
                });
            }
            else
            {
                Dispatcher.Invoke(() => {
                    tbkStatus.Text = "Failed to join color channel with channelId " + channelId + "\n" +
                        (string)response.SelectToken("error.message");
                });
            }
        }

        private async void SendContextToEikon(object sender, SelectionChangedEventArgs e)
        {
            ListBoxItem lbi = ((sender as ListBox).SelectedItem as ListBoxItem);
            string ric = lbi.Content.ToString();
            JObject msg = new JObject(
                              new JProperty("command", "contextChanged"),
                              new JProperty("context",
                                    new JObject(
                                        new JProperty("entities",
                                            new JArray(
                                                new JObject(
                                                    new JProperty("RIC", ric)))))));
            JObject response = await PostMessageToEikon(msg);
            Boolean isSuccess = (Boolean)response.SelectToken("isSuccess");
            if (isSuccess)
            {
                Dispatcher.Invoke(() => { tbkStatus.Text = "Sent the RIC " + ric + " to Eikon"; });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    tbkStatus.Text = "Failed to send RIC " + ric + " to Eikon\n" +
                        (string)response.SelectToken("error.message");
                });
            }
        }
    }
}
