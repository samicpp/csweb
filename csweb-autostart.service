[Unit]
Description=csweb .net webserver
After=network.target

[Service]
Environment="PATH=/usr/local/sbin:/usr/local/bin:/usr/bin:/bin"
WorkingDirectory=/opt/csweb

ExecStart=dotnet run --project web/web.csproj

Restart=on-failure
RestartSec=2
StartLimitBurst=5
StartLimitInterval=10s

Type=simple

StandardOutput=journal
StandardError=journal

User=samicpp
Group=samicpp

[Install]
WantedBy=multi-user.target

