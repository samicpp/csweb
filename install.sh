sudo mkdir -p /opt/csweb
sudo chown -R $USER:$USER /opt/csweb
cd /opt/csweb
git clone https://github.com/samicpp/csweb --recurse-submodules .
sudo ln -s `pwd`/csweb.service /etc/systemd/system
