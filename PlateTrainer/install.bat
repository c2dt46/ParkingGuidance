@echo off
cd /d %~dp0
python -m venv .venv
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
pip install -r requirements.txt
echo.
echo Xong. Chay run.bat de mo ung dung.
pause
