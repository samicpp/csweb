sudo mkdir -p /opt/csweb
sudo chown -R $USER:$USER /opt/csweb
sudo ln -s `pwd`/csweb.service /etc/systemd/system
cd /opt/csweb
git clone https://github.com/samicpp/csweb --recurse-submodules .
