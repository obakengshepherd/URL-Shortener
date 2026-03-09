# URL Shortener Service

## Overview

This project implements a scalable **URL shortening service** similar to Bitly or TinyURL.

The system converts long URLs into short unique identifiers and supports fast redirection at high scale.

---

## Repository Structure

url-shortener-system
├── docs/
│ ├── architecture.md
│ ├── api-spec.md
│ └── scaling.md
│
├── src/
│ ├── Api/
│ ├── Application/
│ ├── Domain/
│ ├── Infrastructure/
│
├── tests/
├── docker/
└── README.md

---

## Core Features

- URL shortening
- redirect service
- click analytics
- custom aliases

---

## System Flow

User submits URL  
↓  
Service generates short code  
↓  
URL stored in database  
↓  
Redirect service resolves short link

---

## Scaling Strategy

- Redis caching
- CDN edge redirects
- load balanced redirect servers
