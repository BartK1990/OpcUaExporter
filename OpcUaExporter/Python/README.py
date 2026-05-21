#!/usr/bin/env python3
"""
setup_python_runtime.ps1 – PowerShell bootstrap (run once after cloning)
----------------------------------------------------------------------
This PowerShell script:
  1. Downloads the official Python 3.11 embeddable package for Windows x64
  2. Extracts it into OpcUaExporter\Python\runtime\
  3. Downloads get-pip.py and installs pip into the embeddable env
  4. Installs 'opcua' (python-opcua) and its dependencies into the runtime

Run from the solution root:
    powershell -ExecutionPolicy Bypass -File setup_python_runtime.ps1
"""

# NOTE: This file is documentation. The actual script is setup_python_runtime.ps1
