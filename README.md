<div align="center">

![Logo](https://i.imgur.com/B03k1rD.png)

[![Build and Release](https://github.com/daglaroglou/Aesir/actions/workflows/release.yml/badge.svg)](https://github.com/daglaroglou/Aesir/actions/workflows/release.yml)
![.NET](https://img.shields.io/badge/.NET-512BD4?style=flat&logo=.net&logoColor=white)
[![Telegram](https://img.shields.io/badge/Telegram-26A5E4?style=flat&logo=telegram&logoColor=white)](https://t.me/+MQU_jKc65TExYjk0)

</div>


Aesir is a powerful cross-platform GUI tool designed to simplify firmware management, ADB operations, and GAPPS installation for Samsung devices. Built with a sleek and intuitive interface, Aesir is your one-stop solution for managing your Samsung devices efficiently.

---

## 🚀 Features

- **Odin Integration**: Flash Samsung firmware with ease using the Odin tab.
- **ADB Tools**: Perform advanced ADB operations like sideloading, debugging, and more.
- **GAPPS Installer**: Install Google Apps packages on custom ROMs effortlessly.
- **Firmware Links**: Quick access to firmware repositories like SamMobile, SamFw, and Frija.
- **Logs Viewer**: Real-time logs with options to save for debugging.
- **Cross-Linux**: Available for both Arch and Debian based distros.

---

## 📦 Installation

### Prerequisites

- [.NET 8.0+ SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (ideally [latest](https://dotnet.microsoft.com/download/dotnet/latest))
- GTK+ 3.0 or higher (for Linux)

### Build and Run

1. Clone the repository:
    ```bash
    git clone https://github.com/daglaroglou/Aesir.git
    cd Aesir
    ```

2. Build the project for all supported architectures:
    ```bash
    bash build.sh
    ```
    This will publish builds for both amd64 and arm64 architectures using the appropriate publish profiles.

3. Run the application for your architecture (replace X.X with your installed .NET version):

    - For amd64:
      ```bash
      sudo ./bin/Release/netX.X/publish/linux-x64/Aesir
      ```

    - For arm64:
      ```bash
      sudo ./bin/Release/netX.X/publish/linux-arm64/Aesir
      ```

## 🤝 Contributing

We welcome contributions! To get started:

1. Fork the repository.
    ```bash
    git clone https://github.com/daglaroglou/Aesir
    ```
2. Create a new branch:
    ```bash
    git checkout -b feature-name
    ```
3. Commit your changes:
    ```bash
    git commit -m "Add feature-name"
    ```
4. Push your branch:
    ```bash
    git push origin feature-name
    ```
5. Open a pull request.

---

### 👨‍💻 Developers

- Christos Daglaroglou - [GitHub](https://github.com/daglaroglou)

---

### 📄 License
This project is licensed under the MIT License.

---

### 🌟 Support
If you find this project helpful, please consider giving it a ⭐ on [GitHub](https://github.com/daglaroglou/Aesir)!

---

## 📧 Contact
For any inquiries or support, reach out via [GitHub Issues](https://github.com/daglaroglou/Aesir/issues).
