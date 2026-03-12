    using DPPMiddleware.ForecourtTcpWorker;
    using DPPMiddleware.IRepository;
    using DppMiddleWareService;
    using System.Windows.Forms;

    public class PopupService : BackgroundService
    {
        private readonly ILogger<PopupService> _logger;
        private readonly IServiceProvider _services;
        private AttendantMonitorWindow? _popupWindow;
        private List<Attendant> _attendants = new();
        private ForecourtClient? _forecourtClient;
        //public event Action<List<string>>? OnUnblockClicked;

        public PopupService(ILogger<PopupService> logger, IServiceProvider services, ForecourtClient forecourtClient)
        {
            _logger = logger;
            _services = services;
            _forecourtClient = forecourtClient;

            _forecourtClient.OnFpStatusUpdated += async (fpId) =>
            {
                await RefreshPopupAsync();
            };

        }

        // This method is called to start the popup and refresh logic
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Popup service starting...");

            _popupWindow = new AttendantMonitorWindow();
            //_popupWindow.OnUnblockClicked += async (selectedAttendants) =>
            //{
            //    HandleFpUnblock(selectedAttendants);
            //    //            OnUnblockClicked?.Invoke(selectedAttendants);
            //};

            //_popupWindow.OnUnblockClicked += async (requests) =>
            //{
            //    await HandleFpUnblock(requests);
            //};
            _popupWindow.OnIsAllowedChanged += async (fpId, isAllowed) =>
            {
                try
                {

                    using var scope = _services.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
                    await repo.UpdateIsAllowedAsync(fpId, isAllowed);

                    await _forecourtClient.CheckAndApplyPumpLimitAsync(fpId, _services, true, isAllowed);

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update IsAllowed for FP={fpId}", fpId);

                    // Optional: revert UI if DB fails
                    _popupWindow.Invoke(new Action(() =>
                    {
                        if (_popupWindow != null)
                        {
                            var toggle = _popupWindow
                                .Controls
                                .OfType<TableLayoutPanel>()
                                .SelectMany(t => t.Controls.OfType<CheckBox>())
                                .FirstOrDefault(c => (int)c.Tag == fpId);

                            if (toggle != null)
                                toggle.Checked = !isAllowed;
                        }
                    }));
                }
            };

            // Start the UI thread to show the popup and handle the attendants
            Task.Run(() => Application.Run(_popupWindow));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Fetch attendants from the service
                    var attendantsWithLimits = await GetAttendantsWithLimitsAsync();

                    // Update the attendants list with new data
                    _attendants.Clear();
                    foreach (var attendant in attendantsWithLimits)
                    {
                        //if (attendant.Status?.Trim().ToLower() == "unavailable")
                        //{
                        _attendants.Add(new Attendant(attendant.FpId, attendant.MaxLimit, attendant.CurrentCount, attendant.Status, attendant.IsAllowed));
                        // }
                    }
                    //_popupWindow?.Invoke(new Action(() =>
                    //{
                    //    _popupWindow.ShowLoader();
                    //}));
                    // Load updated attendants into the popup window (Thread-safe)
                    _popupWindow?.Invoke(new Action(() =>
                    {
                        _popupWindow.LoadAttendants(_attendants);
                        // _popupWindow.HideLoader();

                    }));


                    _logger.LogInformation("Updated attendants list.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating attendants list.");
                }

                // Refresh every 5 seconds
                await Task.Delay(5000, stoppingToken);
            }

            _logger.LogInformation("Popup service stopped.");
        }

        private Task<List<FpLimitDto>> GetAttendantsWithLimitsAsync()
        {
            using var scope = _services.CreateScope();
            var _trans = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
            return _trans.GetTransactionLimitCountByFpId(0);
        }

        private async Task HandleFpUnblock(List<UnblockRequest> requests)
        {
            //foreach (var req in requests)
            //{
            //    int fpId = Convert.ToInt32(req.FpId);

            //    _logger.LogInformation(
            //        "Unblocking FP {FpId} with limit {Limit}",
            //        req.FpId,
            //        req.Limit
            //    );

            await _forecourtClient.HandleFpUnblock(
                _services, requests

            );
            // }
        }

        //private async Task HandleFpUnblock(List<string> selectedAttendants)
        //{
        //    // Handle the unblock logic for selected attendants here
        //    _logger.LogInformation("Unblocking attendants: {0}", string.Join(", ", selectedAttendants));

        //    // Implement your unblocking logic here, for example:
        //    foreach (var fpId in selectedAttendants)
        //    {
        //        int FpId = Convert.ToInt32(fpId);
        //        await _forecourtClient.HandleFpUnblock(_services, new List<string> { fpId });
        //    }

        //    _logger.LogInformation("Unblocking completed for selected attendants.");
        //}


        private async Task RefreshPopupAsync()
        {
            try
            {
                var attendantsWithLimits = await GetAttendantsWithLimitsAsync();

                var updated = attendantsWithLimits
                    //.Where(a => a.Status?.Trim().ToLower() == "unavailable")
                    .Select(a => new Attendant(
                        a.FpId, a.MaxLimit, a.CurrentCount, a.Status, a.IsAllowed))
                    .ToList();

                _attendants = updated;

                if (_popupWindow != null && !_popupWindow.IsDisposed)
                {
                    //_popupWindow.Invoke(new Action(() =>
                    //{
                    //    _popupWindow.ShowLoader();
                    //}));
                    _popupWindow.Invoke(new Action(() =>
                    {
                        _popupWindow.LoadAttendants(_attendants);
                        // _popupWindow.HideLoader();
                    }));
                }

                _logger.LogInformation("Popup refreshed due to FP status update");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Popup refresh failed");
            }
        }

    }
